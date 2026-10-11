using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RoR2;
using RoR2.Networking;
using RoR2.UI;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace RunItBack
{
    // Adds a "RUN IT BACK" button to the defeat screen: reloads the current stage from its
    // start (vanilla scene reload) and restores the snapshot taken when the stage began.
    [BepInPlugin(GUID, NAME, VERSION)]
    public sealed class RunItBackPlugin : BaseUnityPlugin
    {
        public const string GUID = "kel-z.RunItBack";
        public const string NAME = "RunItBack";
        public const string VERSION = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> RewindTimer;
        internal static ConfigEntry<bool> SameStageLayout;

        // State as it was when the current stage began (server side).
        static Snap _stageStart;
        // Set on retry click; consumed by the pre-load hook (see Hooks.SceneChangeBeginning).
        static Snap _pendingRestore;

        sealed class PlayerSnap
        {
            public CharacterMaster Master;
            public int[] Items;
            public uint Money;
            public EquipmentState EquipmentA;
            public EquipmentState EquipmentB;
        }

        // Clones of every RNG stream, captured after GenerateStageRNG but before scene objects
        // (SceneDirector / CombatDirector / ...) consume it in their Awake. Restoring these on the
        // retry load makes the stage roll identically to the attempt that was lost.
        sealed class RngSet
        {
            public Xoroshiro128Plus StageRng;
            public Xoroshiro128Plus NextStageRng;
            public Xoroshiro128Plus BossRewardRng;
            public Xoroshiro128Plus TreasureRng;
            public Xoroshiro128Plus SpawnRng;
            public Xoroshiro128Plus RandomSurvivorOnRespawnRng;
        }

        static RngSet CaptureRng(Run run)
        {
            return new RngSet
            {
                StageRng = CloneRng(run.stageRng),
                NextStageRng = CloneRng(run.nextStageRng),
                BossRewardRng = CloneRng(run.bossRewardRng),
                TreasureRng = CloneRng(run.treasureRng),
                SpawnRng = CloneRng(run.spawnRng),
                RandomSurvivorOnRespawnRng = CloneRng(run.randomSurvivorOnRespawnRng),
            };
        }

        static void ApplyRng(Run run, RngSet rng)
        {
            if (rng.StageRng != null) run.stageRng = new Xoroshiro128Plus(rng.StageRng);
            if (rng.NextStageRng != null) run.nextStageRng = new Xoroshiro128Plus(rng.NextStageRng);
            if (rng.BossRewardRng != null) run.bossRewardRng = new Xoroshiro128Plus(rng.BossRewardRng);
            if (rng.TreasureRng != null) run.treasureRng = new Xoroshiro128Plus(rng.TreasureRng);
            if (rng.SpawnRng != null) run.spawnRng = new Xoroshiro128Plus(rng.SpawnRng);
            if (rng.RandomSurvivorOnRespawnRng != null) run.randomSurvivorOnRespawnRng = new Xoroshiro128Plus(rng.RandomSurvivorOnRespawnRng);
        }

        static Xoroshiro128Plus CloneRng(Xoroshiro128Plus rng) => rng != null ? new Xoroshiro128Plus(rng) : null;

        sealed class Snap
        {
            public readonly List<PlayerSnap> Players = new List<PlayerSnap>();
            public uint TeamLevel;
            public float FixedTime;
            public float RunStopwatch;
            public RngSet Rng;
        }

        static RngSet _loadRng; // RNG state at the most recent stage load, pre-consumption

        // Player-owned minions (drones, turrets, ...) alive when the stage began. On retry,
        // minions acquired during the stage are destroyed (matching the refunded money) and
        // minions that died during the stage are resurrected via a vanilla MasterSummon.
        sealed class MinionSnap
        {
            public CharacterMaster Master;   // fake-null once destroyed
            public string BodyName;
            public CharacterMaster Owner;
        }

        static readonly List<MinionSnap> _stageMinions = new List<MinionSnap>();

        void Awake()
        {
            Log = Logger;
            RewindTimer = Config.Bind("General", "RewindRunTimer", true,
                "Rewind the run/difficulty timer to the stage-start value when retrying.");
            SameStageLayout = Config.Bind("General", "SameStageLayout", true,
                "On retry, restore the RNG state from the stage start so the stage rolls identically (same layout, monsters and elites) instead of continuing the run's RNG stream.");

            // Register the bundled language folder (lang/<lang>/*.txt) with the game's localization.
            string langDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Info.Location) ?? ".", "lang");
            Language.collectLanguageRootFolders += folders =>
            {
                if (System.IO.Directory.Exists(langDir))
                    folders.Add(langDir);
            };

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(Hooks));
            Log.LogInfo($"{NAME} {VERSION} loaded (RewindTimer={RewindTimer.Value} SameStageLayout={SameStageLayout.Value})");
        }

        Harmony _harmony;

        static Snap Capture()
        {
            var snap = new Snap
            {
                FixedTime = Run.instance.fixedTime,
                RunStopwatch = Run.instance.GetRunStopwatch(),
            };
            if (TeamManager.instance)
                snap.TeamLevel = TeamManager.instance.GetTeamLevel(TeamIndex.Player);

            foreach (var pcmc in PlayerCharacterMasterController.instances)
            {
                var master = pcmc ? pcmc.master : null;
                var inv = master ? master.inventory : null;
                if (!inv)
                    continue;
                var ps = new PlayerSnap { Master = master, Money = master.money };
                ps.Items = new int[ItemCatalog.itemCount];
                for (int i = 0; i < ps.Items.Length; i++)
                    ps.Items[i] = inv.GetItemCountPermanent((ItemIndex)i);
                ps.EquipmentA = inv.currentEquipmentState;
                ps.EquipmentB = inv.nextSetEquipmentState;
                snap.Players.Add(ps);
            }

            _stageMinions.Clear();
            foreach (var master in CharacterMaster.readOnlyInstancesList)
                if (master && master.minionOwnership && master.minionOwnership.ownerMaster)
                    _stageMinions.Add(new MinionSnap
                    {
                        Master = master,
                        BodyName = master.bodyPrefab ? master.bodyPrefab.name : null,
                        Owner = master.minionOwnership.ownerMaster,
                    });

            int totalItems = 0;
            foreach (var ps in snap.Players)
                for (int i = 0; i < ps.Items.Length; i++)
                    totalItems += ps.Items[i];
            Log.LogInfo($"[RunItBack] snapshot: players={snap.Players.Count} teamLevel={snap.TeamLevel} runStopwatch={snap.RunStopwatch:F0} totalItems={totalItems} moneyPerPlayer=[{string.Join(",", snap.Players.ConvertAll(p => p.Money))}]");
            return snap;
        }

        static bool IsSnapshotMinion(CharacterMaster master)
        {
            foreach (var e in _stageMinions)
                if (e.Master && e.Master == master)
                    return true;
            return false;
        }

        static void PruneExtraMinions()
        {
            int pruned = 0;
            foreach (var master in CharacterMaster.readOnlyInstancesList)
            {
                if (!master || IsSnapshotMinion(master))
                    continue;
                var ownership = master.minionOwnership;
                if (!ownership || !ownership.ownerMaster)
                    continue;
                var body = master.GetBodyObject();
                if (body)
                    Destroy(body);
                Destroy(master.gameObject);
                pruned++;
            }
            if (pruned > 0)
                Log.LogInfo($"[RunItBack] pruned {pruned} minion(s) acquired during the stage");
        }

        static void ResurrectMissingMinions()
        {
            foreach (var e in _stageMinions)
            {
                if (e.Master || !e.Owner || string.IsNullOrEmpty(e.BodyName))
                    continue; // alive (vanilla respawns it) or nothing to resurrect into

                BodyIndex bodyIndex = BodyCatalog.FindBodyIndex(e.BodyName);
                if (bodyIndex == BodyIndex.None)
                {
                    Log.LogWarning($"[RunItBack] could not resurrect minion: unknown body '{e.BodyName}'");
                    continue;
                }
                var masterIndex = MasterCatalog.FindAiMasterIndexForBody(bodyIndex);
                GameObject masterPrefab = MasterCatalog.GetMasterPrefab(masterIndex);
                if (!masterPrefab)
                {
                    Log.LogWarning($"[RunItBack] could not resurrect minion: no master prefab for '{e.BodyName}'");
                    continue;
                }

                var summoned = new MasterSummon
                {
                    masterPrefab = masterPrefab,
                    position = Vector3.zero, // same fallback vanilla RespawnAllNPCs uses
                    rotation = Quaternion.identity,
                    teamIndexOverride = TeamIndex.Player,
                    ignoreTeamMemberLimit = true,
                }.Perform();
                if (!summoned)
                    continue;
                summoned.minionOwnership.SetOwner(e.Owner);
                Log.LogInfo($"[RunItBack] resurrected minion '{e.BodyName}'");
            }
        }


        static void ApplyInventory(Snap snap)
        {
            var run = Run.instance;
            float dt = run ? run.fixedTime - snap.FixedTime : 0f;

            for (int pi = 0; pi < snap.Players.Count; pi++)
            {
                var ps = snap.Players[pi];
                var inv = ps.Master ? ps.Master.inventory : null;
                if (!inv)
                    continue;
                int itemsBefore = 0;
                for (int i = 0; i < ItemCatalog.itemCount; i++)
                    itemsBefore += inv.GetItemCountPermanent((ItemIndex)i);
                uint moneyBefore = ps.Master.money;
                Log.LogInfo($"[RunItBack] restore player#{pi}: money {moneyBefore}->{ps.Money}");

                int count = Mathf.Min(ItemCatalog.itemCount, ps.Items.Length);
                for (int i = 0; i < count; i++)
                {
                    int target = ps.Items[i];
                    if (inv.GetItemCountPermanent((ItemIndex)i) == target)
                        continue;
                    inv.ResetItemPermanent((ItemIndex)i);
                    if (target > 0)
                        inv.GiveItemPermanent((ItemIndex)i, target);
                }

                RestoreEquipment(inv, ps.EquipmentA, 0u, dt);
                RestoreEquipment(inv, ps.EquipmentB, 1u, dt);

                if (ps.Master)
                    ps.Master.money = ps.Money;
            }

            if (TeamManager.instance)
            {
                uint levelBefore = TeamManager.instance.GetTeamLevel(TeamIndex.Player);
                TeamManager.instance.SetTeamLevel(TeamIndex.Player, snap.TeamLevel);
                Log.LogInfo($"[RunItBack] teamLevel {levelBefore} -> {TeamManager.instance.GetTeamLevel(TeamIndex.Player)} (snapshot {snap.TeamLevel})");
            }
        }

        static void RestoreEquipment(Inventory inv, EquipmentState want, uint set, float dt)
        {
            // Rebase the cooldown onto the current clock so the remaining time is preserved.
            var restored = new EquipmentState(
                want.equipmentIndex,
                want.chargeFinishTime + dt,
                want.charges);
            inv.SetEquipment(restored, new EquipmentLocation { slot = 0u, set = set });
        }

        [HarmonyPatch]
        static class Hooks
        {
            [HarmonyPostfix, HarmonyPatch(typeof(GameEndReportPanelController), "Awake")]
            static void PanelAwake(GameEndReportPanelController __instance) =>
                __instance.gameObject.AddComponent<RunItBackButton>();

            [HarmonyPostfix, HarmonyPatch(typeof(GameEndReportPanelController), "SetDisplayData")]
            static void PanelDataChanged(GameEndReportPanelController __instance) =>
                __instance.GetComponent<RunItBackButton>()?.OnDisplayData(__instance.displayData);

            // Single rewind point: BEFORE the async load wakes any scene object.
            // SceneDirector generates the stage (lockbox counts from held keys, chest contents,
            // prices) from master inventories and the RNG streams -- everything must be
            // stage-start state before it runs.
            [HarmonyPostfix, HarmonyPatch(typeof(NetworkManagerSystem), "ServerChangeScene")]
            static void SceneChangeBeginning()
            {
                var run = Run.instance;
                if (!run)
                    return;

                if (_pendingRestore != null)
                {
                    Log.LogInfo("[RunItBack] pre-load: applying pending restore");
                    if (SameStageLayout.Value && _pendingRestore.Rng != null)
                        ApplyRng(run, _pendingRestore.Rng);
                    ApplyInventory(_pendingRestore);
                    PruneExtraMinions();
                    ResurrectMissingMinions();
                    if (TeamManager.instance)
                    {
                        uint levelBefore = TeamManager.instance.GetTeamLevel(TeamIndex.Player);
                        TeamManager.instance.SetTeamLevel(TeamIndex.Player, _pendingRestore.TeamLevel);
                        Log.LogInfo($"[RunItBack] teamLevel {levelBefore} -> {TeamManager.instance.GetTeamLevel(TeamIndex.Player)} (snapshot {_pendingRestore.TeamLevel})");
                    }
                    if (RewindTimer.Value)
                    {
                        run.SetRunStopwatch(_pendingRestore.RunStopwatch);
                        run.RecalculateDifficultyCoefficent();
                    }
                    _pendingRestore = null;
                }

                if (RewindTimer.Value)
                    Log.LogInfo($"[RunItBack] pre-load: difficultyCoefficient {run.difficultyCoefficient:F2} (costs spawn from this)");

                // Baseline for the scene about to load; on retries this equals the original
                // stage-start values because the restore above just re-created them.
                _stageStart = Capture();
                _loadRng = CaptureRng(run);
            }

            // GameOverController never cleans up the report panels it instantiated under each HUD
            // (they normally die with the scene change). When we cancel the ending / the run ends
            // unexpectedly, remove them ourselves. Idempotent.
            [HarmonyPostfix, HarmonyPatch(typeof(GameOverController), "OnDisable")]
            static void GameOverControllerTornDown()
            {
                foreach (var hud in HUD.readOnlyInstanceList)
                {
                    if (!hud)
                        continue;
                    foreach (var panel in hud.GetComponentsInChildren<GameEndReportPanelController>(true))
                        if (panel)
                            Destroy(panel.gameObject);
                }
            }
        }

        sealed class RunItBackButton : MonoBehaviour
        {
            internal const string LabelToken = "ICYANEON_RUNITBACK_BUTTON_LABEL";

            GameEndReportPanelController _panel;
            Transform _button;

            void Awake() => _panel = GetComponent<GameEndReportPanelController>();

            public void OnDisplayData(GameEndReportPanelController.DisplayData data)
            {
                bool show = ShouldShow(data);
                if (show && !_button)
                    Create();
                if (_button)
                    _button.gameObject.SetActive(show);
            }

            bool ShouldShow(GameEndReportPanelController.DisplayData data)
            {
                if (!NetworkServer.active) return false;           // host / singleplayer only
                if (_stageStart == null) return false;             // no snapshot (installed mid-run)
                var run = Run.instance;
                if (!run) return false;
                var ending = data.runReport != null ? data.runReport.gameEnding : null;
                if (!ending) return false;
                if (ending.isWin) return false;
                // In multiplayer, run types that disconnect on completion misbehave on retry.
                if (!NetworkServer.dontListen && run is EclipseRun or WeeklyRun or InfiniteTowerRun)
                    return false;
                return true;
            }

            void Create()
            {
                var src = _panel && _panel.continueButton ? _panel.continueButton : null;
                if (!src)
                    return;
                var go = Object.Instantiate(src.gameObject, src.transform.parent);
                go.name = "RunItBackButton";
                // Take the continue button's slot: places this button directly to its left.
                go.transform.SetSiblingIndex(src.transform.GetSiblingIndex());

                var btn = go.GetComponent<HGButton>();
                btn.onClick = new Button.ButtonClickedEvent();
                btn.onClick.AddListener(OnClick);

                var labeled = go.GetComponentInChildren<LanguageTextMeshController>();
                if (labeled)
                    labeled.token = LabelToken; // property setter re-renders via UpdateLabel()

                var glyph = go.transform.Find("GenericGlyph");
                if (glyph)
                    glyph.gameObject.SetActive(false);

                _button = go.transform;
            }

            void OnClick()
            {
                if (_pendingRestore != null || _stageStart == null)
                    return;
                var run = Run.instance;
                var gameOver = GameOverController.instance;
                if (!run || !gameOver)
                    return;

                Log.LogInfo($"[RunItBack] retry clicked: arming restore (snapshot players={_stageStart.Players.Count}, rng={( _loadRng != null ? "captured" : "none")})");
                _pendingRestore = _stageStart;
                _pendingRestore.Rng = _loadRng;
                run.isRunning = true;
                Destroy(gameOver.gameObject);
                // Same call vanilla's stage transitions use; triggers the full server reload
                // pipeline; the restore happens pre-load in Hooks.SceneChangeBeginning.
                NetworkManager.singleton.ServerChangeScene(SceneCatalog.mostRecentSceneDef.cachedName);
            }
        }
    }
}
