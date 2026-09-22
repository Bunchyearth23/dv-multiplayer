using DV.InventorySystem;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItem
{
    internal void ReleaseLocalInteraction()
    {
        // Inventory unequip alone does not cover direct world/VR grabs.
        if (Item.IsGrabbed()) Item.ForceEndInteraction();
        if (Inventory.Instance.Contains(gameObject, false))
            Inventory.Instance.DropItemFromHandsOrInventory(gameObject);
    }

}
