using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;    // for Texture2D
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buffs;                // for 1.6 Buff classes
using Omegasis.BuildEndurance.Framework;  // your ModConfig & PlayerData

namespace Omegasis.BuildEndurance
{
    /// <summary>The mod entry point.</summary>
    public class BuildEndurance : Mod
    {
        /*********
        ** Constants & Fields
        *********/
        /// <summary>The unique ID used for our stamina buff.</summary>
        /// <remarks>
        /// Applying another buff with this same ID automatically replaces any existing buff with it.
        /// </remarks>
        private const string EnduranceBuffId = "Omegasis.BuildEndurance/EnduranceBuff";

        /// <summary>The relative path for the current player's data file.</summary>
        private string RelativeDataPath => Path.Combine("data", $"{Constants.SaveFolderName}.json");

        /// <summary>The mod's configuration settings.</summary>
        private ModConfig Config;

        /// <summary>The persistent data for the current player.</summary>
        private PlayerData PlayerData;

        /// <summary>Whether the player has been exhausted today.</summary>
        private bool WasExhausted;

        /// <summary>Whether the player has collapsed today.</summary>
        private bool WasCollapsed;

        /// <summary>Whether the player recently gained XP for tool use.</summary>
        private bool HasRecentToolExp;

        /// <summary>Whether the player was eating last time we checked.</summary>
        private bool WasEating;

        /// <summary>The optional icon used for the buff. Can be null or transparent if you prefer an invisible buff.</summary>
        private Texture2D BuffIconTexture;

        public IModHelper ModHelper;
        public IMonitor ModMonitor;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            // read settings from config.json (create if missing)
            this.Config = helper.ReadConfig<ModConfig>();

            // hook into relevant events
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.Saving += this.OnSaving;

            // load a texture for our buff icon (if you want it visible)
            // if you prefer no visible icon, you can use a transparent 1×1 texture.
            this.BuffIconTexture = this.Helper.ModContent.Load<Texture2D>("assets/stamina-buff.png");

            this.ModHelper = this.Helper;
            this.ModMonitor = this.Monitor;
        }

        /*********
        ** Private methods
        *********/
        /// <summary>Raised after the game state is updated (≈60 times per second).</summary>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return; // no world loaded yet

            // nerf how quickly tool XP is gained
            if (e.IsOneSecond && this.HasRecentToolExp)
                this.HasRecentToolExp = false;

            // give XP when player finishes eating
            if (Game1.player.isEating)
            {
                this.WasEating = true;
            }
            else if (this.WasEating)
            {
                this.PlayerData.CurrentExp += this.Config.ExpForEating;
                this.WasEating = false;
            }

            // give XP once per tool use
            if (!this.HasRecentToolExp && Game1.player.UsingTool)
            {
                this.PlayerData.CurrentExp += this.Config.ExpForToolUse;
                this.HasRecentToolExp = true;
            }

            // give XP when exhausted
            if (!this.WasExhausted && Game1.player.exhausted.Value)
            {
                this.PlayerData.CurrentExp += this.Config.ExpForExhaustion;
                this.WasExhausted = true;
            }

            // give XP when the player collapses or passes out
            if (!this.WasCollapsed && this.shouldFarmerPassout())
            {
                this.PlayerData.CurrentExp += this.Config.ExpForCollapsing;
                this.WasCollapsed = true;
            }
        }

        /// <summary>Raised after the player loads a save slot and the world is initialised.</summary>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // reset daily states
            this.WasExhausted = false;
            this.WasCollapsed = false;
            this.HasRecentToolExp = false;
            this.WasEating = false;

            // read our save data (or create default if none exists)
            this.PlayerData = this.Helper.Data.ReadJsonFile<PlayerData>(this.RelativeDataPath) ?? new PlayerData();

            // track the player's original max stamina if not already set
            if (this.PlayerData.OriginalMaxStamina == 0)
                this.PlayerData.OriginalMaxStamina = Game1.player.MaxStamina;

            // if the user wants to reset
            if (this.PlayerData.ClearModEffects)
            {
                // remove the existing buff entirely
                this.RemoveStaminaBuff();

                // reset fields
                this.PlayerData.ExpToNextLevel = this.Config.ExpToNextLevel;
                this.PlayerData.CurrentExp = this.Config.CurrentExp;
                this.PlayerData.CurrentLevelStaminaBonus = 0;
                this.PlayerData.OriginalMaxStamina = Game1.player.MaxStamina;
                this.PlayerData.BaseStaminaBonus = 0;
                this.PlayerData.CurrentLevel = 0;
                this.PlayerData.ClearModEffects = false;
                this.PlayerData.NightlyStamina = 0;
            }

            // now apply (or refresh) our buff
            this.ApplyStaminaBuffFromData();
        }

        /// <summary>Raised before the game begins writing data to the save file (except for the initial save creation).</summary>
        private void OnSaving(object sender, SavingEventArgs e)
        {
            // reset daily states
            this.WasExhausted = false;
            this.WasCollapsed = false;

            // add XP for sleeping
            this.PlayerData.CurrentExp += this.Config.ExpForSleeping;

            // ensure original max stamina is tracked
            if (this.PlayerData.OriginalMaxStamina == 0)
                this.PlayerData.OriginalMaxStamina = Game1.player.MaxStamina;

            // handle level-ups
            if (this.PlayerData.CurrentLevel < this.Config.MaxLevel)
            {
                while (this.PlayerData.CurrentExp >= this.PlayerData.ExpToNextLevel && this.PlayerData.CurrentLevel < this.Config.MaxLevel)
                {
                    this.PlayerData.CurrentLevel++;
                    this.PlayerData.CurrentExp -= this.PlayerData.ExpToNextLevel;
                    this.PlayerData.ExpToNextLevel *= this.Config.ExpCurve;

                    // previously: Game1.player.MaxStamina += this.Config.StaminaIncreasePerLevel;
                    // now store the stamina bonus for our buff
                    this.PlayerData.CurrentLevelStaminaBonus += this.Config.StaminaIncreasePerLevel;
                }
            }

            // reapply the buff (with updated bonuses)
            int finalBonus = this.ApplyStaminaBuffFromData();

            // store the effective total stamina in PlayerData if we want to keep it consistent next load
            this.PlayerData.NightlyStamina = this.PlayerData.OriginalMaxStamina + finalBonus;

            // save updated data to disk
            this.Helper.Data.WriteJsonFile(this.RelativeDataPath, this.PlayerData);
        }

        /// <summary>Emulate the old <c>Game1.shouldFarmerPassout</c> logic.</summary>
        private bool shouldFarmerPassout()
        {
            return
                Game1.player.stamina <= 0
                || Game1.player.health <= 0
                || Game1.timeOfDay >= 2600;
        }


        /******************************
        **      Buff Logic (1.6)
        ******************************/
        /// <summary>
        /// Calculate the player's total bonus stamina from <see cref="PlayerData"/>, 
        /// then apply a single buff with that bonus.
        /// 
        /// This method returns the final stamina bonus applied.
        /// </summary>
        private int ApplyStaminaBuffFromData()
        {
            // step 1: remove previous buff with the same ID (if any) by overriding it
            // We do NOT necessarily need to remove it first. 
            // Just applying a new buff with the same ID will replace the old one.
            // But if we want to fully remove it, see RemoveStaminaBuff().

            // step 2: figure out how much bonus we want
            int finalBonus = 0;

            // if NightlyStamina > 0, it means we stored an exact final stamina previously
            if (this.PlayerData.NightlyStamina > 0)
            {
                finalBonus = this.PlayerData.NightlyStamina - this.PlayerData.OriginalMaxStamina;
            }
            else
            {
                finalBonus = this.PlayerData.BaseStaminaBonus + this.PlayerData.CurrentLevelStaminaBonus;
            }

            // step 3: create our new buff
            Buff newBuff = new Buff(
                id: EnduranceBuffId,
                displayName: "Build Endurance",      // if visible = false, this won't show
                iconTexture: this.BuffIconTexture,
                iconSheetIndex: 0,
                duration: Buff.ENDLESS,              // last until day ends
                effects: new BuffEffects()
                {
                    MaxStamina = { finalBonus }
                }
            );

            // If you want it invisible:
            // newBuff.visible = false;

            // step 4: apply it (this overwrites any existing buff with the same ID)
            Game1.player.applyBuff(newBuff);

            return finalBonus;
        }

        /// <summary>
        /// Remove our stamina buff entirely by applying a “dummy” buff 
        /// with the same ID and zero duration/stats.
        /// </summary>
        private void RemoveStaminaBuff()
        {
            Buff removalBuff = new Buff(
                id: EnduranceBuffId,
                displayName: "",        // irrelevant since it's removed instantly
                iconTexture: null,      // no icon
                iconSheetIndex: 0,
                duration: 0,            // 0 means it expires immediately
                effects: new BuffEffects() { }
            );
            Game1.player.applyBuff(removalBuff);
        }
    }
}
