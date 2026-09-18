using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using HMLLibrary;
using RaftModLoader;
using Steamworks;
using Unity.Netcode;
using UnityEngine;
using System.Runtime.CompilerServices;

public class BetterStorage : Mod
{
    private const string BaseStorageUniqueName = "Placeable_Storage_Medium";
    private const string NewItemUniqueName = "BetterStorage_Medium";
    private const int NewItemUniqueIndex = 30000;

    internal const int UpgradeStartSlots = 20;
    internal const int UpgradeMaxSlots = 56;
    internal const float MaxSlotScale = 1f;
    internal const float MinSlotScale = 0.625f;

    internal const int UpgradePlankCostPerSlot = 1;

    internal static int GetUpgradePlankCost(int currentSlotCount)
    {
        int slotsAlreadyUpgraded = Mathf.Max(0, currentSlotCount - UpgradeStartSlots);
        return UpgradePlankCostPerSlot + slotsAlreadyUpgraded;
    }

    internal const string NetworkChannelSlug = "BetterStorage_Upgrade";

    static string upgradeKeyName;
    static Keybind upgradeKeyBind;
    public static bool ExtraSettingsAPI_Loaded = false;
    public static bool UpgradeKey => ExtraSettingsAPI_Loaded ? MyInput.GetButtonDown(upgradeKeyName) : Input.GetKeyDown(KeyCode.U);
    public static KeyCode UpgradeMainKey => ExtraSettingsAPI_Loaded ? upgradeKeyBind.MainKey : KeyCode.U;

    public void ExtraSettingsAPI_Load()
    {
        upgradeKeyName = ExtraSettingsAPI_GetKeybindName("upgrade");
        upgradeKeyBind = ExtraSettingsAPI_GetKeybind("upgrade");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public string ExtraSettingsAPI_GetKeybindName(string SettingName) => "";
    [MethodImpl(MethodImplOptions.NoInlining)]
    public Keybind ExtraSettingsAPI_GetKeybind(string SettingName) => null;

    internal static readonly Dictionary<uint, int> RememberedSlotCounts = new Dictionary<uint, int>();

    private static BetterStorage _instance;

    private static string SlotCountSavePath =>
        _instance != null ? Path.Combine(_instance.DataFolder, "BetterStorage_SlotCounts.json") : null;

    internal static void SaveSlotCounts()
    {
        string path = SlotCountSavePath;
        if (path == null)
        {
            return;
        }

        if (RememberedSlotCounts.Count == 0 && File.Exists(path))
        {
            Debug.LogWarning("[BetterStorage] Skipping save: in-memory slot counts are empty but a non-empty save file already exists -- refusing to overwrite it.");
            return;
        }

        try
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<uint, int> kvp in RememberedSlotCounts)
            {
                sb.Append(kvp.Key).Append(':').Append(kvp.Value).Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[BetterStorage] Saved {RememberedSlotCounts.Count} slot-count entries to {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[BetterStorage] Failed to save slot-count data: {e}");
        }
    }

    internal static void LoadSlotCounts()
    {
        RememberedSlotCounts.Clear();
        string path = SlotCountSavePath;
        if (path == null || !File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                int sep = line.IndexOf(':');
                if (sep < 0)
                {
                    continue;
                }

                if (uint.TryParse(line.Substring(0, sep).Trim(), out uint objectIndex) &&
                    int.TryParse(line.Substring(sep + 1).Trim(), out int slotCount))
                {
                    RememberedSlotCounts[objectIndex] = slotCount;
                }
            }
            Debug.Log($"[BetterStorage] Loaded {RememberedSlotCounts.Count} slot-count entries from {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[BetterStorage] Failed to load slot-count data: {e}");
        }
    }

    public override void WorldEvent_WorldLoaded()
    {
        base.WorldEvent_WorldLoaded();
        LoadSlotCounts();
    }

    public override void WorldEvent_WorldSaved()
    {
        base.WorldEvent_WorldSaved();
        SaveSlotCounts();
    }

    private static Transform _prefabParent;

    public static Item_Base BetterStorageItem;
    private Harmony harmony;

    private static readonly List<GameObject> _clonedBlockPrefabs = new List<GameObject>();

    public void Start()
    {
        _instance = this;
        LoadSlotCounts();
        SubscribeToNetworkChannel(NetworkChannelSlug);

        try
        {
            Item_Base baseItem = ItemManager.GetItemByName(BaseStorageUniqueName);
            if (baseItem == null)
            {
                Debug.LogError($"[BetterStorage] Base item '{BaseStorageUniqueName}' doesn't exist -- can't clone it.");
                return;
            }

            Item_Base customItem = UnityEngine.Object.Instantiate(baseItem);
            customItem.name = NewItemUniqueName;

            customItem.UniqueName = NewItemUniqueName;
            customItem.UniqueIndex = NewItemUniqueIndex;

            if (customItem.settings_Inventory != null)
            {
                customItem.settings_Inventory.DisplayName = "BetterStorage (Medium)";
            }
            else
            {
                Debug.LogError("[BetterStorage] customItem.settings_Inventory is null -- cannot set display name.");
            }

            try
            {
                Item_Base existing = ItemManager.GetItemByIndex(NewItemUniqueIndex);
                if (existing != null)
                    Debug.LogWarning($"[BetterStorage] An item already exists at index {NewItemUniqueIndex} ('{existing.name}') -- pick a different NewItemUniqueIndex, this one is taken.");
            }
            catch { }

            var settingsBuildable = customItem.settings_buildable;
            if (settingsBuildable == null)
            {
                Debug.LogError("[BetterStorage] Cloned item has no settings_buildable -- aborting.");
                return;
            }

            Block[] originalBlocks = settingsBuildable.GetBlockPrefabs();
            if (originalBlocks == null || originalBlocks.Length == 0)
            {
                Debug.LogError("[BetterStorage] Cloned item's settings_buildable has no block prefabs to clone.");
                return;
            }

            Array blockPrefabsField = settingsBuildable.blockPrefabs;
            if (blockPrefabsField == null)
            {
                Debug.LogError("[BetterStorage] Could not find the underlying 'blockPrefabs' field to replace with cloned blocks.");
                return;
            }

            if (_prefabParent == null)
            {
                GameObject prefabParentGO = new GameObject("BetterStorage_PrefabParent");
                prefabParentGO.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(prefabParentGO);
                _prefabParent = prefabParentGO.transform;
            }

            Type elementType = blockPrefabsField.GetType().GetElementType();
            Array clonedBlocksArray = Array.CreateInstance(elementType, blockPrefabsField.Length);

            for (int i = 0; i < blockPrefabsField.Length; i++)
            {
                object originalEntry = blockPrefabsField.GetValue(i);
                Block originalBlock = originalEntry as Block;
                if (originalBlock == null)
                {

                    clonedBlocksArray.SetValue(originalEntry, i);
                    Debug.LogWarning($"[BetterStorage] blockPrefabs[{i}] wasn't a Block -- left unmodified (still points at the original).");
                    continue;
                }

                GameObject clonedGO = UnityEngine.Object.Instantiate(originalBlock.gameObject, _prefabParent);
                clonedGO.name = originalBlock.gameObject.name + "_" + NewItemUniqueName;
                _clonedBlockPrefabs.Add(clonedGO);

                Block clonedBlock = clonedGO.GetComponent<Block>();

                if (clonedBlock != null)
                {
                    clonedBlock.buildableItem = customItem;
                    Item_Base originalReturnItem = originalBlock.itemToReturnOnDestroy;
                    if (originalReturnItem == null || originalReturnItem == baseItem)
                    {
                        clonedBlock.itemToReturnOnDestroy = customItem;
                    }
                }

                clonedBlocksArray.SetValue(clonedBlock, i);
            }

            settingsBuildable.blockPrefabs = (Block[])clonedBlocksArray;

            RAPI.RegisterItem(customItem);
            BetterStorageItem = customItem;

            int patchedQuadTypes = 0;
            foreach (SO_BlockQuadType quadType in Resources.FindObjectsOfTypeAll<SO_BlockQuadType>())
            {
                if (quadType.acceptableBlockTypes.Contains(baseItem) &&
                    !quadType.acceptableBlockTypes.Contains(customItem))
                {
                    quadType.acceptableBlockTypes.Add(customItem);
                    patchedQuadTypes++;
                }
            }
            Debug.Log($"[BetterStorage] Whitelisted '{NewItemUniqueName}' on {patchedQuadTypes} SO_BlockQuadType asset(s) that already accepted '{BaseStorageUniqueName}'.");

            Item_Base plank = ItemManager.GetItemByName("Plank");
            Item_Base hinge = ItemManager.GetItemByName("Hinge");
            if (plank == null || hinge == null)
            {
                Debug.LogError($"[BetterStorage] Could not resolve recipe ingredients (Plank found: {plank != null}, Hinge found: {hinge != null}). Recipe not set -- item registered but uncraftable until this is fixed.");
            }
            else
            {
                customItem.SetRecipe(
                    new[]
                    {
                        new CostMultiple(new[] { plank }, 20),
                        new CostMultiple(new[] { hinge }, 2)
                    },
                    CraftingCategory.Resources,
                    1,
                    true,
                    null,
                    0
                );
            }

            harmony = new Harmony("com.voltaccept.raft.BetterStorage");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Debug.Log($"[BetterStorage] Registered '{NewItemUniqueName}' as a clone of '{BaseStorageUniqueName}' with {clonedBlocksArray.Length} independently-cloned block prefab(s). Recipe: 20 Plank + 2 Hinge.");
        }
        catch (Exception e)
        {
            Debug.LogError(e);
        }
    }

    private void LateUpdate()
    {
        BetterStorage_UpgradeHint.LateTick();
    }

    internal static void SendUpgradeNetworkMessage(BetterStorage_UpgradeMessage message)
    {
        _instance?.SendNetworkMessage(message, Target.Other, EP2PSend.k_EP2PSendReliable, NetworkChannelSlug);
    }

    public override bool OnNetworkMessage(object message, Network_UserId from, string modslug)
    {
        if (modslug != NetworkChannelSlug || !(message is BetterStorage_UpgradeMessage upgradeMsg))
        {
            return false;
        }

        StorageManager storageManager = ComponentManager<Network_Player>.Value?.StorageManager;
        Storage_Small chest = storageManager?.GetStorageByObjectIndex(upgradeMsg.storageObjectIndex);
        if (chest == null)
        {
            Debug.LogError("[BetterStorage] Received an upgrade message for a chest that could not be found (object index " + upgradeMsg.storageObjectIndex + ").");
            return false;
        }

        Inventory inv = chest.GetInventoryReference();
        if (inv == null)
        {
            return false;
        }

        int safety = UpgradeMaxSlots;
        while (inv.allSlots.Count < upgradeMsg.newSlotCount && inv.allSlots.Count < UpgradeMaxSlots && safety-- > 0)
        {
            if (AddSlotToInventory(inv) == null)
            {
                break;
            }
        }
        ResizeInventoryWindowToFitSlots(inv);

        RememberedSlotCounts[chest.ObjectIndex] = inv.allSlots.Count;
        if (Raft_Network.IsHost)
        {
            SaveSlotCounts();
        }

        return true;
    }

    public void OnModUnload()
    {
        harmony?.UnpatchAll("com.voltaccept.raft.BetterStorage");
        foreach (GameObject go in _clonedBlockPrefabs)
            if (go != null)
                UnityEngine.Object.Destroy(go);
        _clonedBlockPrefabs.Clear();
        SaveSlotCounts();
        _instance = null;
        Debug.Log("[BetterStorage] Mod has been unloaded!");
    }

    internal static Slot AddSlotToInventory(Inventory inventory)
    {
        if (inventory == null)
        {
            return null;
        }

        if (inventory.slotPrefab == null || inventory.gridLayoutGroup == null)
        {
            Debug.LogError("[BetterStorage] Inventory is missing slotPrefab or gridLayoutGroup -- cannot add a slot.");
            return null;
        }

        GameObject slotGO = UnityEngine.Object.Instantiate(inventory.slotPrefab, inventory.gridLayoutGroup.transform);
        Slot slot = slotGO.GetComponent<Slot>();
        if (slot == null)
        {
            Debug.LogError("[BetterStorage] inventory.slotPrefab has no Slot component -- cannot add a slot.");
            UnityEngine.Object.Destroy(slotGO);
            return null;
        }

        slot.InitializeEventListeners(inventory);
        inventory.allSlots.Add(slot);
        return slot;
    }

    private class InventoryResizeState
    {
        public float baseHeight;
        public float baseGridHeight;
        public int baseSlotCount;
        public int baseColumns;
        public Vector2 baseCellSize;
        public Vector2 baseSpacing;
        public float availableWidth;
    }

    private static readonly ConditionalWeakTable<Inventory, InventoryResizeState> _resizeStates = new ConditionalWeakTable<Inventory, InventoryResizeState>();

    internal static void ResizeInventoryWindowToFitSlots(Inventory inventory)
    {
        if (inventory == null || inventory.gridLayoutGroup == null || inventory.invRectTransform == null)
        {
            Debug.LogError("[BetterStorage] Cannot resize inventory window -- missing gridLayoutGroup or invRectTransform.");
            return;
        }

        var grid = inventory.gridLayoutGroup;
        RectTransform gridRect = grid.GetComponent<RectTransform>();

        if (!_resizeStates.TryGetValue(inventory, out InventoryResizeState state))
        {
            int baseColumns = grid.constraintCount > 0 ? grid.constraintCount : 1;
            Vector2 baseCellSize = grid.cellSize;
            Vector2 baseSpacing = grid.spacing;

            state = new InventoryResizeState
            {
                baseHeight = inventory.invRectTransform.sizeDelta.y,
                baseGridHeight = gridRect.sizeDelta.y,
                baseSlotCount = inventory.allSlots.Count,
                baseColumns = baseColumns,
                baseCellSize = baseCellSize,
                baseSpacing = baseSpacing,
                availableWidth = baseColumns * baseCellSize.x + (baseColumns - 1) * baseSpacing.x
            };
            _resizeStates.Add(inventory, state);
        }

        int totalSlots = inventory.allSlots.Count;
        float t = 0f;
        int slotRange = BetterStorage.UpgradeMaxSlots - BetterStorage.UpgradeStartSlots;
        if (slotRange > 0)
        {
            t = Mathf.Clamp01((float)(totalSlots - BetterStorage.UpgradeStartSlots) / slotRange);
        }
        float targetScale = Mathf.Lerp(BetterStorage.MaxSlotScale, BetterStorage.MinSlotScale, t);
        int columns = Mathf.Max(state.baseColumns, Mathf.RoundToInt(state.baseColumns / targetScale));

        float newCellWidth = (state.availableWidth - (columns - 1) * state.baseSpacing.x) / columns;
        float actualScale = Mathf.Clamp(newCellWidth / state.baseCellSize.x, BetterStorage.MinSlotScale, BetterStorage.MaxSlotScale);
        Vector2 cellSize = new Vector2(state.baseCellSize.x * actualScale, state.baseCellSize.y * actualScale);
        int rows = Mathf.CeilToInt((float)totalSlots / columns);

        grid.constraintCount = columns;
        grid.cellSize = cellSize;

        foreach (Slot slot in inventory.allSlots)
        {
            ApplyHighlightScale(slot, actualScale);
        }

        if (inventory.hoverTransform != null)
        {
            inventory.hoverTransform.sizeDelta = cellSize;
        }
        if (inventory.darkenedTransform != null)
        {
            inventory.darkenedTransform.sizeDelta = cellSize;
        }

        float rowSize = cellSize.y + state.baseSpacing.y;
        float gridHeight = rows * rowSize;

        Vector2 gridSizeDelta = gridRect.sizeDelta;
        gridSizeDelta.y = gridHeight;
        gridRect.sizeDelta = gridSizeDelta;

        float chromeHeight = state.baseHeight - state.baseGridHeight;
        float windowHeight = chromeHeight + gridHeight;

        Vector2 sizeDelta = inventory.invRectTransform.sizeDelta;
        sizeDelta.y = windowHeight;
        inventory.invRectTransform.sizeDelta = sizeDelta;

        Debug.Log($"[BetterStorage] Inventory window resized: {state.baseSlotCount}->{totalSlots} slots, columns {state.baseColumns}->{columns}, rows->{rows}, cell size {state.baseCellSize}->{cellSize}, window height {state.baseHeight}->{windowHeight}.");
    }

    private static void ApplyHighlightScale(Slot slot, float scale)
    {
        if (slot == null || slot.imageFocused == null)
        {
            return;
        }

        RectTransform highlightRect = slot.imageFocused.rectTransform;
        if (highlightRect == null)
        {
            return;
        }

        Vector3 currentScale = highlightRect.localScale;
        highlightRect.localScale = new Vector3(scale, scale, currentScale.z);
    }

}

static class BetterStorage_ExtensionMethods
{

    public static void SetRecipe(
        this Item_Base item,
        CostMultiple[] cost,
        CraftingCategory category = CraftingCategory.Resources,
        int amountToCraft = 1,
        bool learnedFromBeginning = false,
        string subCategory = null,
        int subCatergoryOrder = 0
    )
    {
        var recipe = item.settings_recipe;
        recipe.craftingCategory = category;
        recipe.amountToCraft = amountToCraft;
        recipe.learnedFromBeginning = learnedFromBeginning;
        recipe.subCategory = subCategory;
        recipe.subCatergoryOrder = subCatergoryOrder;
        recipe.NewCost = cost;
    }
}

[HarmonyPatch(typeof(OccupyingComponent), "SetNewMaterial")]
static class OccupyingComponent_SetNewMaterial_ForceInit
{
    static void Prefix(OccupyingComponent __instance)
    {
        bool needsInit = __instance.renderers == null || __instance.renderers.Length == 0;
        if (needsInit)
        {
            try { __instance.Start(); }
            catch (Exception e) { Debug.LogError($"[BetterStorage] Force-Start on '{__instance.gameObject.name}' failed: {e}"); }
        }
    }
}

[HarmonyPatch(typeof(Storage_Small), "OnFinishedPlacement")]
static class Storage_Small_OnFinishedPlacement_BetterStorageInit
{
    static void Postfix(Storage_Small __instance)
    {
        if (__instance.buildableItem == null || __instance.buildableItem.UniqueName != "BetterStorage_Medium")
        {
            return;
        }

        Inventory inv = __instance.GetInventoryReference();
        if (inv == null)
        {
            return;
        }

        BetterStorage.ResizeInventoryWindowToFitSlots(inv);

        int targetSlots = BetterStorage.UpgradeStartSlots;
        if (BetterStorage.RememberedSlotCounts.TryGetValue(__instance.ObjectIndex, out int remembered))
        {
            targetSlots = Mathf.Max(targetSlots, remembered);
        }
        targetSlots = Mathf.Min(targetSlots, BetterStorage.UpgradeMaxSlots);

        int safety = targetSlots;
        while (inv.allSlots.Count < targetSlots && safety-- > 0)
        {
            if (BetterStorage.AddSlotToInventory(inv) == null)
            {
                break;
            }
        }

        BetterStorage.ResizeInventoryWindowToFitSlots(inv);
    }
}

internal static class BetterStorage_UpgradeHint
{
    private static bool _shown;
    private static CostCollection _costUI;
    private static Item_Base _plankItem;

    private static CostCollection GetOrCreateCostUI()
    {
        if (_costUI == null)
        {
            var obj = ComponentManager<BuildMenu>.Value.costColletionCursor.gameObject;
            _costUI = UnityEngine.Object.Instantiate(obj, ComponentManager<CanvasHelper>.Value.transform).GetComponent<CostCollection>();
            _costUI.transform.position = obj.transform.position;
            _costUI.GetComponent<RectTransform>().offsetMin = obj.GetComponent<RectTransform>().offsetMin;
            _costUI.GetComponent<RectTransform>().offsetMax = obj.GetComponent<RectTransform>().offsetMax;
            _costUI.GetComponent<RectTransform>().anchorMin = obj.GetComponent<RectTransform>().anchorMin;
            _costUI.GetComponent<RectTransform>().anchorMax = obj.GetComponent<RectTransform>().anchorMax;
            _costUI.gameObject.SetActive(false);
        }
        return _costUI;
    }

    private static void ShowCostUI(int plankCost)
    {
        if (_plankItem == null)
        {
            _plankItem = ItemManager.GetItemByName("Plank");
            if (_plankItem == null)
            {
                Debug.LogError("[BetterStorage] Could not find item 'Plank' -- cost UI will not be shown.");
                return;
            }
        }

        CostCollection costUI = GetOrCreateCostUI();
        costUI.gameObject.SetActive(true);
        costUI.ShowCost(new CostMultiple[]
        {
            new CostMultiple(new[] { _plankItem }, plankCost)
        });
    }

    private static void HideCostUI()
    {
        if (_costUI != null)
        {
            _costUI.gameObject.SetActive(false);
        }
    }

    internal static void LateTick()
    {
        Camera cam = Camera.main;
        if (cam == null || CanvasHelper.ActiveMenu != MenuType.None)
        {
            Clear();
            return;
        }

        if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, Player.UseDistance + 0.5f, LayerMasks.MASK_Block))
        {
            Clear();
            return;
        }

        Storage_Small storage = hit.collider.GetComponentInParent<Block>() as Storage_Small;
        if (storage == null || storage.buildableItem == null || storage.buildableItem.UniqueName != "BetterStorage_Medium" || storage.IsOpen)
        {
            Clear();
            return;
        }

        Inventory inv = storage.GetInventoryReference();
        if (inv == null || inv.allSlots.Count >= BetterStorage.UpgradeMaxSlots)
        {
            Clear();
            return;
        }

        Network_Player localPlayer = ComponentManager<Network_Player>.Value;
        PlayerInventory playerInventory = localPlayer?.Inventory;
        if (playerInventory == null)
        {
            Clear();
            return;
        }

        DisplayTextManager dtm = ComponentManager<DisplayTextManager>.Value;
        if (dtm == null)
        {
            return;
        }

        int plankCost = BetterStorage.GetUpgradePlankCost(inv.allSlots.Count);
        bool canAfford = Cheat.UseGodMode || playerInventory.GetItemCount("Plank") >= plankCost;

        dtm.ShowText(
            $"Upgrade ({inv.allSlots.Count}/{BetterStorage.UpgradeMaxSlots}",
            BetterStorage.UpgradeMainKey,
            2, 0, false);
        _shown = true;

        ShowCostUI(plankCost);

        if (canAfford && BetterStorage.UpgradeKey)
        {
            DoUpgrade(storage, inv, playerInventory, localPlayer, plankCost);
        }
    }

    private static void Clear()
    {
        if (!_shown) return;
        _shown = false;
        try { ComponentManager<DisplayTextManager>.Value?.HideDisplayTexts(2); } catch { }
        HideCostUI();
    }

    private static void DoUpgrade(Storage_Small storage, Inventory inv, PlayerInventory playerInventory, Network_Player localPlayer, int plankCost)
    {
        if (!Cheat.UseGodMode)
        {
            playerInventory.RemoveItem("Plank", plankCost);
        }

        Slot newSlot = BetterStorage.AddSlotToInventory(inv);
        if (newSlot == null)
        {
            return;
        }
        BetterStorage.ResizeInventoryWindowToFitSlots(inv);

        BetterStorage.RememberedSlotCounts[storage.ObjectIndex] = inv.allSlots.Count;
        BetterStorage.SaveSlotCounts();
        Debug.Log($"[BetterStorage] Upgraded '{storage.gameObject.name}' to {inv.allSlots.Count} slots.");

        BetterStorage_UpgradeMessage upgradeMessage = new BetterStorage_UpgradeMessage
        {
            storageObjectIndex = storage.ObjectIndex,
            newSlotCount = inv.allSlots.Count
        };

        BetterStorage.SendUpgradeNetworkMessage(upgradeMessage);
    }
}

[HarmonyPatch(typeof(RGD_Storage), nameof(RGD_Storage.RestoreInventory))]
static class RGD_Storage_RestoreInventory_BetterStorageGrow
{
    static void Prefix(RGD_Storage __instance, Inventory inventory)
    {
        if (inventory == null || __instance.slots == null)
        {
            return;
        }

        int neededSlots = 0;
        foreach (RGD_Slot slot in __instance.slots)
        {
            if (slot != null && slot.slotIndex + 1 > neededSlots)
            {
                neededSlots = slot.slotIndex + 1;
            }
        }

        neededSlots = Mathf.Min(neededSlots, BetterStorage.UpgradeMaxSlots);

        int safety = neededSlots;
        while (inventory.allSlots.Count < neededSlots && safety-- > 0)
        {
            if (BetterStorage.AddSlotToInventory(inventory) == null)
            {
                break;
            }
        }
        BetterStorage.ResizeInventoryWindowToFitSlots(inventory);
    }
}

[Serializable]
internal class BetterStorage_UpgradeMessage
{
    public uint storageObjectIndex;
    public int newSlotCount;
}