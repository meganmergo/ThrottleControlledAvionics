/* Name: Throttle Controlled Avionics, Fork by Allis Tauri
 *
 * Authors: Quinten Feys & Willem van Vliet & Allis Tauri
 * License: BY: Attribution-ShareAlike 3.0 Unported (CC BY-SA 3.0): 
 * http://creativecommons.org/licenses/by-sa/3.0/
 * 
 */

using System;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	[KSPAddon(KSPAddon.Startup.Flight, false)]
	public class ThrottleControlledAvionics : MonoBehaviour, ITCAModule
	{
		const string TCA_PART = "ThrottleControlledAvionics";

		TCAGui GUI;

		public VesselWrapper vessel { get; private set; }
		public TCAGlobals GLB { get { return TCAConfiguration.Globals; } }
		public VesselConfig CFG { get { return vessel.CFG; } }
		public TCAState State { get { return vessel.State; } set { vessel.State = value; } }
		public void SetState(TCAState state) { vessel.State |= state; }
		public bool IsStateSet(TCAState state) { return vessel.IsStateSet(state); }

		#region Modules
		TorqueOptimizer trq;
		VerticalSpeedControl vsc;
		HorizontalSpeedControl hsc;
		AltitudeControl alt;
		#endregion

		#region Public Info
		public float TorqueError { get { return trq == null? 0f : trq.TorqueError; } }
		public bool  Available { get { return vessel != null && vessel.TCA_Available; } }
		public bool  Controllable { get { return vessel != null && vessel.Controllable; } }
		#endregion

		#region Initialization
		#if DEBUG
		public void OnReloadGlobals()
		{
			if(vessel == null) return;
			trq.Init(); vsc.Init(); hsc.Init(); alt.Init();
		}
		#endif

		public void Awake()
		{
			TCAConfiguration.Load();
			GameEvents.onVesselChange.Add(onVesselChange);
			GameEvents.onVesselWasModified.Add(onVesselModify);
			GameEvents.onGameStateSave.Add(onSave);
		}

		internal void OnDestroy() 
		{ 
			TCAConfiguration.Save();
			if(GUI != null) GUI.OnDestroy();
			GameEvents.onVesselChange.Remove(onVesselChange);
			GameEvents.onVesselWasModified.Remove(onVesselModify);
			GameEvents.onGameStateSave.Remove(onSave);
		}

		void onVesselChange(Vessel vsl)
		{ 
			if(vsl == null || vsl.Parts == null) return;
			save(); 
			reset();
			vessel = new VesselWrapper(vsl);
			init();
		}

		void onVesselModify(Vessel vsl)
		{ if(vessel.vessel == vsl) init(); }

		void onSave(ConfigNode node) { save(); }

		void save() 
		{ 
			TCAConfiguration.Save(); 
			if(GUI != null) GUI.SaveConfig();
		}

		void reset()
		{
			if(vessel != null) 
			{
				vessel.OnAutopilotUpdate -= block_throttle;
				hsc.DisconnectAutopilot();
			}
			vessel = null; trq = null; vsc = null; hsc = null; alt = null;
		}

		void init()
		{
			vessel.TCA_Available = false;
			trq = new TorqueOptimizer(vessel);
			vsc = new VerticalSpeedControl(vessel);
			hsc = new HorizontalSpeedControl(vessel);
			alt = new AltitudeControl(vessel);
			trq.Init();	vsc.Init();	hsc.Init();	alt.Init();
			hsc.ConnectAutopilot();
			vessel.OnAutopilotUpdate += block_throttle;
			if(!vessel.isEVA && 
			   (!GLB.IntegrateIntoCareer ||
			    Utils.PartIsPurchased(TCA_PART)))
			{
				vessel.UpdateEngines();
				if(vessel.Engines.Count > 0)
				{
					if(GUI == null) GUI = new TCAGui(this);
					Utils.Log("TCA is enabled");
					vessel.TCA_Available = true;
					return;
				}
			} 
			if(GUI != null) { GUI.OnDestroy(); GUI = null; }
			Utils.Log("TCA is disabled");
//			Utils.Log("TCA is disabled.\nVessel is EVA: {0}; TCA available in TechTree: {1}; Engines count: {2}",
//			          vessel.isEVA, 
//			          (!GLB.IntegrateIntoCareer ||
//			            Utils.PartIsPurchased(TCA_PART)),
//			          Engines.Count);//debug
		}
		#endregion

		#region Controls
		public void ActivateTCA(bool state)
		{
			if(state == CFG.Enabled) return;
			CFG.Enabled = state;
			if(!CFG.Enabled) //reset engine limiters
			{
				vessel.Engines.ForEach(e => e.forceThrustPercentage(100));
				State = TCAState.Disabled;
			}
		}
		public void ToggleTCA() { ActivateTCA(!CFG.Enabled); }

		public void BlockThrottle(bool state)
		{
			if(state == CFG.BlockThrottle) return;
			CFG.BlockThrottle = state;
			if(CFG.BlockThrottle && !CFG.VerticalSpeedControl)
				CFG.VerticalCutoff = 0;
		}

		public void KillHorizontalVelocity(bool state)
		{
			if(state == CFG.KillHorVel) return;
			CFG.KillHorVel = state;
			if(CFG.KillHorVel)
				CFG.SASWasEnabled = vessel.ActionGroups[KSPActionGroup.SAS];
			else if(CFG.SASWasEnabled)
				vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
		}
		public void ToggleHvAutopilot() { KillHorizontalVelocity(!CFG.KillHorVel); }

		public void MaintainAltitude(bool state)
		{
			if(state == CFG.ControlAltitude) return;
			CFG.ControlAltitude = state;
			if(CFG.ControlAltitude)
			{
				vessel.UpdateAltitude();
				CFG.DesiredAltitude = vessel.Altitude;
			}
		}
		public void ToggleAltitudeAutopilot() { MaintainAltitude(!CFG.ControlAltitude); }

		public void AltitudeAboveTerrain(bool state)
		{
			if(state == CFG.AltitudeAboveTerrain) return;
			CFG.AltitudeAboveTerrain = state;
			vessel.UpdateAltitude();
			if(CFG.AltitudeAboveTerrain)
				CFG.DesiredAltitude -= vessel.TerrainAltitude;
			else CFG.DesiredAltitude += vessel.TerrainAltitude;
		}
		public void ToggleAltitudeAboveTerrain() { AltitudeAboveTerrain(!CFG.AltitudeAboveTerrain);}
		#endregion

		public void OnGUI() 
		{ 
			if(!vessel.TCA_Available) return;
			Styles.Init();
			if(vessel.Controllable) GUI.DrawGUI(); 
			TCAToolbarManager.UpdateToolbarButton();
		}

		public void Update()
		{ 
			if(!vessel.Controllable) return;
			GUI.OnUpdate();
			if(CFG.Enabled && CFG.BlockThrottle)
			{
				if(CFG.ControlAltitude)
				{
					if(GameSettings.THROTTLE_UP.GetKey())
						CFG.DesiredAltitude = Mathf.Lerp(CFG.DesiredAltitude, 
						                                 CFG.DesiredAltitude+10, 
						                                 CFG.VSControlSensitivity);
					else if(GameSettings.THROTTLE_DOWN.GetKey())
						CFG.DesiredAltitude = Mathf.Lerp(CFG.DesiredAltitude,
						                                 CFG.DesiredAltitude-10, 
						                                 CFG.VSControlSensitivity);
					else if(GameSettings.THROTTLE_FULL.GetKeyDown())
						CFG.DesiredAltitude = CFG.DesiredAltitude+10;
					else if(GameSettings.THROTTLE_CUTOFF.GetKeyDown())
						CFG.DesiredAltitude = CFG.DesiredAltitude-10;
				}
				else
				{
					if(GameSettings.THROTTLE_UP.GetKey())
						CFG.VerticalCutoff = Mathf.Lerp(CFG.VerticalCutoff, 
						                                GLB.VSC.MaxSpeed, 
						                                CFG.VSControlSensitivity);
					else if(GameSettings.THROTTLE_DOWN.GetKey())
						CFG.VerticalCutoff = Mathf.Lerp(CFG.VerticalCutoff, 
						                                -GLB.VSC.MaxSpeed, 
						                                CFG.VSControlSensitivity);
					else if(GameSettings.THROTTLE_FULL.GetKeyDown())
						CFG.VerticalCutoff = GLB.VSC.MaxSpeed;
					else if(GameSettings.THROTTLE_CUTOFF.GetKeyDown())
						CFG.VerticalCutoff = -GLB.VSC.MaxSpeed;
				}
			}
		}

		void update_modules_state()
		{
			trq.UpdateState();
			vsc.UpdateState();
			hsc.UpdateState();
			alt.UpdateState();
		}

		public void FixedUpdate()
		{
			if(!vessel.TCA_Available || !CFG.Enabled) return;
			State = TCAState.Enabled;
			if(vessel.ctrlState.mainThrottle <= 0) return;
			SetState(TCAState.Throttled);
			if(!vessel.ElectricChargeAvailible) return;
			SetState(TCAState.HaveEC);
			if(!vessel.CheckEngines()) return;
			SetState(TCAState.HaveActiveEngines);
			vessel.UpdateCommons();
			update_modules_state();
			if(hsc.IsActive) 
				vessel.UpdateRTorqueLimits();
			if(CFG.AutoTune || hsc.IsActive) 
				vessel.UpdateRotationalStats();
			if(vsc.IsActive || alt.IsActive) 
				vessel.UpdateOnPlanetStats();
			alt.Update(); vsc.Update();
			vessel.InitEngines();
			vessel.SortEngines();
			//balance-only engines
			vessel.UpdateTorque(vessel.ManualEngines);
			if(vessel.BalancedEngines.Count > 0)
				trq.OptimizeEngines(vessel.BalancedEngines, Vector3.zero);
			vessel.UpdateTorque(vessel.ManualEngines, vessel.BalancedEngines);
			vessel.NormalizeLimits &= vessel.SteeringEngines.Count > vessel.ManeuverEngines.Count;
			//optimize limits for steering
			vessel.UpdateETorqueLimits();
			trq.SteerWithEngines();
			vessel.SetThrustLimiters();
		}

		void block_throttle(FlightCtrlState s)
		{ if(vessel.TCA_Available && CFG.Enabled && CFG.BlockThrottle) s.mainThrottle = 1f; }
	}

	/// <summary>
	/// Binary flags of TCA state.
	/// They should to be checked in this particular order, as they are set sequentially:
	/// If a previous flag is not set, the next ones are not either.
	/// </summary>
	[Flags] public enum TCAState 
	{ 
		Disabled 			   = 0,
		Enabled 			   = 1 << 0,
		Throttled 			   = 1 << 1,
		HaveEC 				   = 1 << 2, 
		HaveActiveEngines 	   = 1 << 3,
		VerticalSpeedControl   = 1 << 4,
		AltitudeControl        = 1 << 5,
		LoosingAltitude 	   = 1 << 6,
		Unoptimized			   = 1 << 7,
		Nominal				   = Enabled | Throttled | HaveEC | HaveActiveEngines,
		NoActiveEngines        = Enabled | Throttled | HaveEC,
		NoEC                   = Enabled | Throttled,
	}
}
