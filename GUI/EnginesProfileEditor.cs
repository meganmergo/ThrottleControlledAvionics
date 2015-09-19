﻿//   EnginesPrfileEditor.cs
//
//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2015 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	[KSPAddon(KSPAddon.Startup.EditorAny, false)]
	public class EnginesProfileEditor : AddonWindowBase<EnginesProfileEditor>
	{
		const string LockName = "EnginesProfileEditor";
		const string DefaultConstractName = "Untitled Space Craft";

		NamedConfig CFG;
		readonly List<EngineWrapper> Engines = new List<EngineWrapper>();

		public static bool GUIVisible 
		{ 
			get { return instance != null && instance.CFG != null && instance.CFG.GUIVisible; } 
			set { if(instance != null && instance.CFG != null) instance.CFG.GUIVisible = value; }
		}

		public override void Awake()
		{
			base.Awake();
			height = 400;
			GameEvents.onEditorShipModified.Add(OnShipModified);
			GameEvents.onEditorLoad.Add(OnShipLoad);
			GameEvents.onEditorRestart.Add(Reset);
		}

		public override void OnDestroy ()
		{
			GameEvents.onEditorShipModified.Remove(OnShipModified);
			GameEvents.onEditorLoad.Remove(OnShipLoad);
			GameEvents.onEditorRestart.Remove(Reset);
			base.OnDestroy();
		}

		void Reset()
		{
			TCAToolbarManager.ShowButton(false);
			Engines.Clear();
			CFG = null;
		}

		void OnShipLoad(ShipConstruct ship, CraftBrowser.LoadType load_type)
		{
			if(load_type == CraftBrowser.LoadType.Merge) return;
			if(UpdateEngines(ship)) GetCFG(ship);
		}

		void GetCFG(ShipConstruct ship)
		{
			var TCA_Modules = ModuleTCA.AllTCA(ship.Parts);
			if(TCA_Modules.Count == 0) { Reset(); return; }
			CFG = null;
			foreach(var tca in TCA_Modules)
			{
				if(tca.CFG == null) continue;
				CFG = NamedConfig.FromVesselConfig(ship.shipName, tca.CFG);
				break;
			}
			if(CFG == null)
			{
				CFG = new NamedConfig(ship.shipName);
				CFG.EnginesProfiles.AddProfile(Engines);
			}
			else CFG.ActiveProfile.Apply(Engines);
			CFG.ActiveProfile.Update(Engines);
			UpdateCFG(TCA_Modules);
		}

		void UpdateCFG(IList<ModuleTCA> TCA_Modules)
		{
			if(CFG == null || TCA_Modules.Count == 0) return;
			TCA_Modules.ForEach(m => m.CFG = null);
			TCA_Modules[0].CFG = CFG;
		}
		void UpdateCFG(ShipConstruct ship)
		{ UpdateCFG(ModuleTCA.AllTCA(ship.Parts)); }

		bool UpdateEngines(ShipConstruct ship)
		{
			Engines.Clear();
			if(ModuleTCA.HasTCA) 
			{ 
				TCAToolbarManager.SetDefaultButton();
				TCAToolbarManager.ShowButton();
				foreach(Part p in ship.Parts)
					foreach(var module in p.Modules)
					{	
						var engine = module as ModuleEngines;
						if(engine != null) Engines.Add(new EngineWrapper(engine)); 
					}
				if(Engines.Count > 0) return true;
			}
			Reset();
			return false;
		}

		void OnShipModified(ShipConstruct ship)
		{
			if(!UpdateEngines(ship)) return;
			if(CFG == null) GetCFG(ship);
			else UpdateCFG(ship);
			CFG.ActiveProfile.Update(Engines);
		}

		protected override void DrawMainWindow(int windowID)
		{
			CFG.EnginesProfiles.Draw(height);
			if(CFG.ActiveProfile.Changed)
				CFG.ActiveProfile.Apply(Engines);
			base.DrawMainWindow(windowID);
		}

		public void OnGUI()
		{
			if(Engines.Count == 0 || !CFG.GUIVisible || !showHUD) 
			{
				Utils.LockIfMouseOver(LockName, MainWindow, false);
				return;
			}
			Styles.Init();
			Utils.LockIfMouseOver(LockName, MainWindow);
			MainWindow = 
				GUILayout.Window(GetInstanceID(), 
					MainWindow, 
					DrawMainWindow, 
					TCATitle,
					GUILayout.Width(width),
					GUILayout.Height(height));
			MainWindow.clampToScreen();
		}
	}
}