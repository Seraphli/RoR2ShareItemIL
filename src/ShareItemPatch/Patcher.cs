using RoR2;

namespace ShareItemPatch
{
    /// <summary>
    /// Payload that replaces the single-player item grant inside
    /// <c>RoR2.ItemDef.AttemptGrant</c>.
    ///
    /// The Cecil patcher injects this type into RoR2.dll and rewrites the
    /// <c>Inventory.GiveItemPermanent(itemIndex, 1)</c> call site so that the
    /// picked-up item is granted to every player instead of only the picker.
    ///
    /// Lunar / Void items are excluded: those are still granted to the picker
    /// only, preserving the original game behaviour.
    /// </summary>
    public static class Patcher
    {
        public static void Grant(Inventory pickerInventory, ItemIndex itemIndex)
        {
            ItemDef itemDef = ItemCatalog.GetItemDef(itemIndex);

            bool excluded = itemDef == null
                || itemDef.tier == ItemTier.Lunar
                || itemDef.tier == ItemTier.VoidTier1
                || itemDef.tier == ItemTier.VoidTier2
                || itemDef.tier == ItemTier.VoidTier3
                || itemDef.tier == ItemTier.VoidBoss;

            if (excluded)
            {
                if (pickerInventory != null)
                {
                    pickerInventory.GiveItemPermanent(itemIndex, 1);
                }
                return;
            }

            var instances = PlayerCharacterMasterController.instances;
            for (int i = 0; i < instances.Count; i++)
            {
                CharacterMaster master = instances[i].master;
                if (master != null && master.inventory != null)
                {
                    master.inventory.GiveItemPermanent(itemIndex, 1);
                }
            }
        }
    }
}
