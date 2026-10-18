using Mono.Cecil;
using Mono.Cecil.Cil;

// ShareItemPatcher
// ---------------
// Locates the single-player item grant inside RoR2.ItemDef.AttemptGrant and
// rewrites it so the picked-up item is granted to every player.
//
// The share logic lives in the ShareItemPatch helper assembly (plain C#, so the
// compiler type-checks it against the game's public API). This tool injects that
// helper type *into* RoR2.dll and rewrites the call site, so the final artifact
// is a single RoR2.dll -- no extra file to install.
//
// Usage:
//   ShareItemPatcher <input RoR2.dll> <ShareItemPatch.dll> <output RoR2.dll> [assemblySearchDir]

internal static class Program
{
    private const string HelperTypeName = "ShareItemPatch.Patcher";
    private const string HelperMethodName = "Grant";
    private const string TargetTypeName = "RoR2.ItemDef";
    private const string TargetMethodName = "AttemptGrant";

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: ShareItemPatcher <input RoR2.dll> <ShareItemPatch.dll> <output RoR2.dll> [assemblySearchDir]");
            return 1;
        }

        string input = Path.GetFullPath(args[0]);
        string helperPath = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        string searchDir = args.Length > 3 ? Path.GetFullPath(args[3]) : Path.GetDirectoryName(input)!;

        try
        {
            Patch(input, helperPath, output, searchDir);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            if (Environment.GetEnvironmentVariable("SHAREITEM_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
            }
            return 2;
        }
    }

    private static void Patch(string input, string helperPath, string output, string searchDir)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(searchDir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);

        using var helperAsm = AssemblyDefinition.ReadAssembly(helperPath);
        var helperType = helperAsm.MainModule.Types.FirstOrDefault(t => t.FullName == HelperTypeName)
            ?? throw new InvalidOperationException($"'{HelperTypeName}' not found in {helperPath}");
        var helperMethod = helperType.Methods.FirstOrDefault(m => m.Name == HelperMethodName)
            ?? throw new InvalidOperationException($"'{HelperTypeName}.{HelperMethodName}' not found");

        using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;
        string selfName = module.Assembly.Name.Name;
        int refsBefore = module.AssemblyReferences.Count;

        // ---- 1. inject the helper type into RoR2.dll -------------------------
        var injected = InjectType(module, helperType);
        var grant = injected.Methods.First(m => m.Name == HelperMethodName);
        Console.WriteLine($"[+] injected {injected.FullName}.{grant.Name}({string.Join(", ", grant.Parameters.Select(p => p.ParameterType.Name))})");

        // ---- 2. locate the item-grant call site inside AttemptGrant ----------
        var itemDefType = module.Types.FirstOrDefault(t => t.FullName == TargetTypeName)
            ?? throw new InvalidOperationException($"'{TargetTypeName}' not found");
        var attemptGrant = itemDefType.Methods.FirstOrDefault(m => m.Name == TargetMethodName && m.HasBody)
            ?? throw new InvalidOperationException($"{TargetTypeName}.{TargetMethodName} (with body) not found");

        var body = attemptGrant.Body;
        var instrs = body.Instructions;

        Instruction? call = instrs.FirstOrDefault(i =>
            i.OpCode == OpCodes.Callvirt &&
            i.Operand is MethodReference mr &&
            mr.DeclaringType.Name == "Inventory" &&
            mr.Name == "GiveItemPermanent" &&
            mr.Parameters.Count == 2 &&
            mr.Parameters[0].ParameterType.Name == "ItemIndex" &&
            mr.Parameters[1].ParameterType.Name == "Int32");

        if (call is null)
            throw new InvalidOperationException(
                $"Could not find 'Inventory.GiveItemPermanent(ItemIndex, Int32)' inside {attemptGrant.FullName}. " +
                "The game code changed; re-check the injection point (see README).");

        int idx = instrs.IndexOf(call);
        if (idx < 2)
            throw new InvalidOperationException("Unexpected instruction layout around the grant call.");

        var constOne = instrs[idx - 1];
        var itemIndexLoad = instrs[idx - 2];

        if (constOne.OpCode != OpCodes.Ldc_I4_1)
            throw new InvalidOperationException($"Expected 'ldc.i4.1' before the grant call, found '{constOne.OpCode}'.");

        int itemIndexLocal = GetLocalIndex(itemIndexLoad);
        var itemIndexVar = body.Variables[itemIndexLocal];
        if (itemIndexVar.VariableType.FullName != "RoR2.ItemIndex")
            throw new InvalidOperationException(
                $"Expected an 'RoR2.ItemIndex' local before the grant call, found '{itemIndexVar.VariableType.FullName}'.");

        Console.WriteLine($"[+] call site at IL_{call.Offset:X4}: {itemIndexLoad.OpCode} {itemIndexVar.VariableType.Name} ; ldc.i4.1 ; callvirt GiveItemPermanent");

        // ---- 3. rewrite: replace 'ldloc ; ldc.i4.1 ; callvirt' with 'ldloc ; call Grant' --
        var il = body.GetILProcessor();
        var newItemIndexLoad = il.Create(OpCodes.Ldloc, itemIndexVar);
        var callGrant = il.Create(OpCodes.Call, grant);
        il.InsertBefore(call, newItemIndexLoad);
        il.InsertBefore(call, callGrant);
        il.Remove(itemIndexLoad);
        il.Remove(constOne);
        il.Remove(call);

        // ---- 4. sanity checks ------------------------------------------------
        if (module.AssemblyReferences.Count != refsBefore &&
            module.AssemblyReferences.Any(r => r.Name == selfName))
            throw new InvalidOperationException($"Refusing to write: the patch created a self-reference to '{selfName}'.");

        VerifyStack(attemptGrant);
        VerifyStack(grant);

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        asm.Write(output);
        Console.WriteLine($"[+] wrote {output}");
    }

    // ------------------------------------------------------------------ helpers

    private static TypeDefinition InjectType(ModuleDefinition target, TypeDefinition source)
    {
        var newType = new TypeDefinition(
            source.Namespace,
            source.Name,
            source.Attributes,
            ImportType(target, source.BaseType));

        target.Types.Add(newType);

        foreach (var f in source.Fields)
        {
            newType.Fields.Add(new FieldDefinition(f.Name, f.Attributes, ImportType(target, f.FieldType)));
        }

        foreach (var m in source.Methods)
        {
            var nm = new MethodDefinition(m.Name, m.Attributes, ImportType(target, m.ReturnType))
            {
                ImplAttributes = m.ImplAttributes,
            };
            foreach (var p in m.Parameters)
            {
                nm.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, ImportType(target, p.ParameterType)));
            }
            newType.Methods.Add(nm);
            CopyBody(target, m, nm);
        }

        return newType;
    }

    private static void CopyBody(ModuleDefinition target, MethodDefinition source, MethodDefinition destination)
    {
        if (!source.HasBody) return;
        if (source.Body.HasExceptionHandlers)
            throw new NotSupportedException($"'{source.FullName}' has exception handlers; the type injector does not support them.");

        var sbody = source.Body;
        var dbody = new MethodBody(destination)
        {
            InitLocals = sbody.InitLocals,
            MaxStackSize = sbody.MaxStackSize,
        };
        destination.Body = dbody;

        var varMap = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var v in sbody.Variables)
        {
            var nv = new VariableDefinition(ImportType(target, v.VariableType));
            dbody.Variables.Add(nv);
            varMap[v] = nv;
        }

        var insMap = new Dictionary<Instruction, Instruction>();
        foreach (var ins in sbody.Instructions)
        {
            var nins = Instruction.Create(OpCodes.Nop);
            nins.OpCode = ins.OpCode;
            insMap[ins] = nins;
        }

        var il = dbody.GetILProcessor();
        foreach (var ins in sbody.Instructions)
        {
            var nins = insMap[ins];
            nins.Operand = ImportOperand(target, ins.Operand, varMap, insMap);
            il.Append(nins);
        }
    }

    private static object? ImportOperand(
        ModuleDefinition target,
        object? operand,
        Dictionary<VariableDefinition, VariableDefinition> varMap,
        Dictionary<Instruction, Instruction> insMap)
    {
        switch (operand)
        {
            case null:
                return null;
            case Instruction i:
                return insMap[i];
            case Instruction[] arr:
                return arr.Select(x => insMap[x]).ToArray();
            case VariableDefinition v:
                return varMap[v];
            case TypeReference tr:
                return ImportType(target, tr);
            case FieldReference fr:
                return ImportField(target, fr);
            case MethodReference mr:
                return ImportMethod(target, mr);
            case ParameterDefinition p:
                throw new NotSupportedException($"Parameter operand '{p.Name}' is not supported.");
            default:
                return operand; // primitives, strings, CallSite, ...
        }
    }

    private static TypeReference ImportType(ModuleDefinition target, TypeReference tr)
    {
        if (tr is GenericInstanceType git)
        {
            var element = ImportType(target, git.ElementType);
            var ngit = new GenericInstanceType(element);
            foreach (var arg in git.GenericArguments)
            {
                ngit.GenericArguments.Add(ImportType(target, arg));
            }
            return ngit;
        }

        if (tr is ArrayType at)
        {
            return new ArrayType(ImportType(target, at.ElementType), at.Rank);
        }

        // Reuse the module's own definition instead of creating a self-reference.
        var own = target.GetType(tr.FullName);
        if (own != null) return own;

        return target.ImportReference(tr);
    }

    private static FieldReference ImportField(ModuleDefinition target, FieldReference fr)
    {
        var ownType = target.GetType(fr.DeclaringType.FullName);
        if (ownType != null)
        {
            var def = ownType.Fields.FirstOrDefault(f => f.Name == fr.Name);
            if (def != null) return def;
        }
        return target.ImportReference(fr);
    }

    private static MethodReference ImportMethod(ModuleDefinition target, MethodReference mr)
    {
        // Same-module type: reuse the existing definition.
        var ownType = target.GetType(mr.DeclaringType.FullName);
        if (ownType != null)
        {
            var def = FindMethod(ownType, mr);
            if (def != null) return def;
        }

        // Generic instance (e.g. ReadOnlyCollection<PlayerCharacterMasterController>):
        // rebuild the reference so generic arguments map to the target module.
        if (mr.DeclaringType is GenericInstanceType)
        {
            var declaring = (GenericInstanceType)ImportType(target, mr.DeclaringType);
            var rebuilt = new MethodReference(mr.Name, ImportSignatureType(target, mr.ReturnType, declaring), declaring)
            {
                HasThis = mr.HasThis,
                ExplicitThis = mr.ExplicitThis,
                CallingConvention = mr.CallingConvention,
            };
            foreach (var p in mr.Parameters)
            {
                rebuilt.Parameters.Add(new ParameterDefinition(ImportSignatureType(target, p.ParameterType, declaring)));
            }
            return rebuilt;
        }

        return target.ImportReference(mr);
    }

    /// <summary>
    /// Imports a signature type, substituting a generic parameter of the
    /// declaring type with the matching generic argument of <paramref name="declaring"/>
    /// (e.g. <c>T</c> in <c>ReadOnlyCollection&lt;T&gt;.get_Item</c> becomes the real element type).
    /// </summary>
    private static TypeReference ImportSignatureType(ModuleDefinition target, TypeReference type, GenericInstanceType declaring)
    {
        if (type is GenericParameter gp
            && gp.Type == GenericParameterType.Type
            && gp.Position < declaring.GenericArguments.Count)
        {
            return declaring.GenericArguments[gp.Position];
        }
        return ImportType(target, type);
    }

    private static MethodDefinition? FindMethod(TypeDefinition type, MethodReference mr)
    {
        return type.Methods.FirstOrDefault(m =>
            m.Name == mr.Name &&
            m.IsStatic == mr.HasThis == false &&
            m.Parameters.Count == mr.Parameters.Count &&
            m.Parameters.Select(p => p.ParameterType.Name)
                .SequenceEqual(mr.Parameters.Select(p => p.ParameterType.Name)));
    }

    private static int GetLocalIndex(Instruction instruction)
    {
        if (instruction.OpCode == OpCodes.Ldloc_0) return 0;
        if (instruction.OpCode == OpCodes.Ldloc_1) return 1;
        if (instruction.OpCode == OpCodes.Ldloc_2) return 2;
        if (instruction.OpCode == OpCodes.Ldloc_3) return 3;
        if (instruction.OpCode == OpCodes.Ldloc_S || instruction.OpCode == OpCodes.Ldloc)
            return ((VariableDefinition)instruction.Operand).Index;
        throw new InvalidOperationException($"Expected a local load instruction, found '{instruction.OpCode}'.");
    }

    // ------------------------------------------------------------------ verifier

    /// <summary>
    /// Walks the IL, simulates the evaluation stack and fails on underflow,
    /// max-stack violation or inconsistent stack depth at a join point.
    /// </summary>
    private static void VerifyStack(MethodDefinition method)
    {
        var body = method.Body;
        var stackAt = new Dictionary<Instruction, int>();
        var queue = new Queue<Instruction>();

        var first = body.Instructions[0];
        stackAt[first] = 0;
        queue.Enqueue(first);

        while (queue.Count > 0)
        {
            var ins = queue.Dequeue();
            int before = stackAt[ins];
            (int pop, int push) = StackDelta(ins);
            int after = before - pop + push;

            if (after < 0)
                throw new InvalidOperationException($"IL verification failed in {method.FullName}: stack underflow at IL_{ins.Offset:X4} ({ins.OpCode}).");
            if (after > body.MaxStackSize)
                throw new InvalidOperationException($"IL verification failed in {method.FullName}: stack depth {after} exceeds maxstack {body.MaxStackSize} at IL_{ins.Offset:X4}.");

            var next = new List<Instruction>();
            if (ins.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
            {
                switch (ins.Operand)
                {
                    case Instruction target: next.Add(target); break;
                    case Instruction[] targets: next.AddRange(targets); break;
                }
            }
            if (ins.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw))
            {
                next.Add(ins.Next);
            }

            foreach (var n in next)
            {
                if (n == null) continue;
                if (!stackAt.TryGetValue(n, out int known))
                {
                    stackAt[n] = after;
                    queue.Enqueue(n);
                }
                else if (known != after)
                {
                    throw new InvalidOperationException($"IL verification failed in {method.FullName}: inconsistent stack depth at IL_{n.Offset:X4} ({known} vs {after}).");
                }
            }
        }
    }

    private static (int Pop, int Push) StackDelta(Instruction ins)
    {
        if (ins.OpCode is { } op && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj) && ins.Operand is MethodReference mr)
        {
            int pop = mr.Parameters.Count + (op == OpCodes.Newobj ? 0 : mr.HasThis ? 1 : 0);
            int push = (op == OpCodes.Newobj || mr.ReturnType.FullName != "System.Void") ? 1 : 0;
            return (pop, push);
        }

        return (PopCount(ins.OpCode.StackBehaviourPop), PushCount(ins.OpCode.StackBehaviourPush));
    }

    private static int PopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
            or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
            or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
            or StackBehaviour.Popref_popi_popref => 3,
        _ => 0,
    };

    private static int PushCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8
            or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => 0,
    };
}
