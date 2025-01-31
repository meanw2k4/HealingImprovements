using Comfort.Common;
using EFT.HealthSystem;
using EFT;
using SPT.Reflection.Patching;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace HealingImprovements
{
    [BepInPlugin("com.meanw.healingimprovements", "Healing Improvements", "1.0.0")]
    public class HealingImprovements : BaseUnityPlugin
    {

        public static ConfigEntry<bool> HealLimbs { get; set; }
        public static ConfigEntry<int> HealDelay { get; set; }
        public static ConfigEntry<bool> AutoHealCanceling;
        public static Player MainPlayer { get; private set; }
        public static ActiveHealthController ActiveHealthController { get; private set; }
        public static void SetPlayer(Player p) => MainPlayer = p;
        public static void SetPlayerHealthController(ActiveHealthController controller) => ActiveHealthController = controller;

        protected void Awake()
        {

            HealLimbs = Config.Bind("MAIN", "Heal Limbs", true, new ConfigDescription("If surgery kits should also be continuous.\nNOTE: Animation does not loop."));
            HealDelay = Config.Bind("MAIN", "Heal Delay", 0, new ConfigDescription("The delay between every heal on each limb. Game default is 2, set to 0 to use the intended behavior.", new AcceptableValueRange<int>(0, 5)));
            AutoHealCanceling = Config.Bind("MAIN", "Automatic Heal Canceling", true, new ConfigDescription("Stops the healing animation when health is full."));

            new ContinuousHealing_EndHeal_Patch().Enable();
            new ContinuousHealing_CancelHeal_Patch().Enable();
            new ContinuousHealing_StartHeal_Patch().Enable();
            new HealingAutoCancelPatch().Enable();
        }
    }

    internal class HealingAutoCancelPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(GameWorld).GetMethod("OnGameStarted", BindingFlags.Public | BindingFlags.Instance);
        }

        [PatchPostfix]
        public static void PostFix()
        {
            if (HealingImprovements.AutoHealCanceling.Value == true)
            {
                GameWorld gameWorld = Singleton<GameWorld>.Instance;
                HealingImprovements.SetPlayer(gameWorld.MainPlayer);
                HealingImprovements.SetPlayerHealthController(gameWorld.MainPlayer.ActiveHealthController);
                HealingImprovements.ActiveHealthController.HealthChangedEvent += ActiveHealthController_HealthChangedEvent;
            }
        }

        private static void ActiveHealthController_HealthChangedEvent(EBodyPart bodyPart, float amount, DamageInfoStruct damageInfo)
        {
            if (damageInfo.DamageType != EDamageType.Medicine)
                return;

            MedsItemClass medkitInHands = HealingImprovements.MainPlayer.TryGetItemInHands<MedsItemClass>();

            if (medkitInHands != null && !HealingImprovements.ActiveHealthController.IsBodyPartBroken(bodyPart))
            {
                ValueStruct bodyPartHealth = HealingImprovements.ActiveHealthController.GetBodyPartHealth(bodyPart);
                var effects = HealingImprovements.ActiveHealthController.BodyPartEffects.Effects[bodyPart];
                bool bleeding = effects.ContainsKey("LightBleeding") || effects.ContainsKey("HeavyBleeding");

                bool healingItemDepleted = medkitInHands.MedKitComponent.HpResource < 1;

                if ((bodyPartHealth.AtMaximum && !bleeding) || healingItemDepleted)
                    HealingImprovements.ActiveHealthController.RemoveMedEffect();
            }
        }
    }

    internal class ContinuousHealing_CancelHeal_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(GControl4).GetMethod(nameof(GControl4.CancelApplyingItem));
        }

        [PatchPrefix]
        public static void Prefix(Player ___Player)
        {
            if (___Player.IsYourPlayer)
            {
                ContinuousHealing_EndHeal_Patch.cancelRequested = true;
            }
        }
    }

    internal class ContinuousHealing_StartHeal_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(Player.MedsController).GetMethod(nameof(Player.MedsController.Spawn));
        }

        [PatchPrefix]
        public static void Prefix(Player ____player)
        {
            if (____player.IsYourPlayer)
            {
                ContinuousHealing_EndHeal_Patch.cancelRequested = false;
            }
        }
    }

    internal class ContinuousHealing_EndHeal_Patch : ModulePatch
    {
        private static FieldInfo playerField;

        public static bool cancelRequested = false;

        protected override MethodBase GetTargetMethod()
        {
            playerField = AccessTools.Field(typeof(Player.MedsController), "_player");
            return typeof(Player.MedsController.Class1173).GetMethod(nameof(Player.MedsController.Class1173.method_8));
        }

        [PatchPrefix]
        public static bool Prefix(Player.MedsController.Class1173 __instance, Player.MedsController ___medsController_0, IEffect effect, Callback<IOnHandsUseCallback> ___callback_0)
        {
            if (cancelRequested)
            {
                return true;
            }

            if (effect is not GInterface349)
            {
                return false;
            }

            Player player = (Player)playerField.GetValue(___medsController_0);
            if (player == null)
            {
                return true;
            }

            if (!player.IsYourPlayer)
            {
                return true;
            }

            if (___medsController_0.Item is not MedKitItemClass && (!HealingImprovements.HealLimbs.Value || ___medsController_0.Item is not MedicalItemClass))
            {
                return true;
            }

            MedsItemClass medsItem = (MedsItemClass)___medsController_0.Item;
            if (medsItem == null)
            {
                return true;
            }

            if (medsItem.MedKitComponent == null)
            {
                return true;
            }

            if (medsItem.MedKitComponent.HpResource <= 1 && medsItem.MedKitComponent.MaxHpResource < 95)
            {
                return true;
            }

            if (player.ActiveHealthController.CanApplyItem(___medsController_0.Item, EBodyPart.Common))
            {
                player.HealthController.EffectRemovedEvent -= __instance.method_8;

                float originalDelay = ActiveHealthController.GClass2808.GClass2818_0.MedEffect.MedKitStartDelay;

                ActiveHealthController.GClass2808.GClass2818_0.MedEffect.MedKitStartDelay = (float)HealingImprovements.HealDelay.Value;

                IEffect newEffect = player.ActiveHealthController.DoMedEffect(___medsController_0.Item, EBodyPart.Common, 1f);
                if (newEffect == null)
                {
                    __instance.State = Player.EOperationState.Finished;
                    ___medsController_0.FailedToApply = true;
                    Callback<IOnHandsUseCallback> callbackToRun = ___callback_0;
                    ___callback_0 = null;
                    callbackToRun(___medsController_0);
                    ActiveHealthController.GClass2808.GClass2818_0.MedEffect.MedKitStartDelay = originalDelay;
                    return false;
                };

                player.HealthController.EffectRemovedEvent += __instance.method_8;

                ActiveHealthController.GClass2808.GClass2818_0.MedEffect.MedKitStartDelay = originalDelay;

                return false;
            }

            return true;
        }
    }
}
