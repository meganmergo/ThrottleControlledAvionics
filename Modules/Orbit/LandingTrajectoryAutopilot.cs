﻿//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.
//
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using AT_Utils;

namespace ThrottleControlledAvionics
{
	public abstract class LandingTrajectoryAutopilot : TargetedTrajectoryCalculator<LandingTrajectory>
	{
		public new class Config : TCAModule.ModuleConfig
		{
			[Persistent] public float Dtol               = 1000f; //m
			[Persistent] public float FlyOverAlt         = 1000;  //m
			[Persistent] public float ApproachAlt        = 250;   //m

			[Persistent] public float BrakeThrustThreshod = 100;  //m/s
			[Persistent] public float BrakeEndSpeed      = 10;    //m/s
			[Persistent] public float MinBrakeOffset     = 10;    //m/s
			[Persistent] public float FinalBrakeOffset   = 5;     //m/s
            [Persistent] public float LandingThrustTime  = 3;     //s
			[Persistent] public float ParachutesDeployOffset = 10; //s

			[Persistent] public float CorrectionOffset   = 20f;   //s
			[Persistent] public float CorrectionTimer    = 10f;   //s
			[Persistent] public float CorrectionMinDv    = 0.5f;  //m/s
			[Persistent] public float CorrectionThrustF  = 2.0f;
			[Persistent] public float CorrectionTimeF    = 2f;
			[Persistent] public float CorrectionDirF     = 2f;

			[Persistent] public float ObstacleBrakeF     = 1.1f;
			[Persistent] public float HoverTimeThreshold = 60f;   //s
			[Persistent] public float DropBallastThreshold = 0.5f;//dP/P_asl
			[Persistent] public float MaxDPressure       = 3f;    //kPa
			[Persistent] public float MinDPressure       = 1f;    //kPa
			[Persistent] public float MachThreshold      = 0.9f;

            [Persistent] public float ScanningAngle      = 21;
			[Persistent] public int   PointsPerFrame     = 5;
            [Persistent] public int   AtmoTrajectoryResolution = 5;

			[Persistent] public float HeatingCoefficient = 0.02f;
		}
		protected static Config LTRJ { get { return Globals.Instance.LTRJ; } }

		protected LandingTrajectoryAutopilot(ModuleTCA tca) : base(tca) {}

		public enum LandingStage { None, Start, Wait, Decelerate, Coast, HardLanding, SoftLanding, Approach, Land, LandHere }
		[Persistent] public LandingStage landing_stage;

        [Persistent] public FloatField CorrectionMaxDist = new FloatField(min:0);
		[Persistent] public bool UseChutes = true;
		[Persistent] public bool UseBrakes = true;
		[Persistent] public bool CorrectTarget = true;
		[Persistent] public bool LandASAP;
		public bool ShowSettings;

		protected Timer DecelerationTimer = new Timer(0.5);
		protected Timer CollisionTimer = new Timer(1);
		protected Timer StageTimer = new Timer(5);
		protected Timer NoEnginesTimer = new Timer(1);
		protected ManeuverExecutor Executor;
        protected PQS_Scanner_CDOS scanner;
		protected AtmoSim sim;
		protected bool scanned, flat_target;
		protected double PressureASL;

		protected Timer dP_up_timer = new Timer(1);
		protected Timer dP_down_timer = new Timer(1);
		protected double dP_threshold;
		protected double landing_deadzone;
        protected double terminal_velocity;
		protected double last_dP;
		protected double rel_dP;
		protected float last_Err;

        protected bool vessel_within_range;
        protected bool vessel_after_target;
        protected bool target_within_range;
        protected bool landing_before_target;

		protected AttitudeControl ATC;
		protected ThrottleControl THR;
		protected BearingControl  BRC;
		protected AutoLander      LND;
        protected CollisionPreventionSystem CPS;

		protected double TargetAltitude { get { return CFG.Target.SurfaceAlt(Body); } }

		protected override LandingTrajectory CurrentTrajectory
		{ get { return new LandingTrajectory(VSL, Vector3d.zero, VSL.Physics.UT, CFG.Target, TargetAltitude, false); } }


        protected abstract class LandingSiteOptimizerBase : TrajectoryOptimizer
        {
            protected readonly LandingTrajectoryAutopilot module;
            protected readonly double dtol;

            public LandingTrajectory Best { get; protected set; }

            public string Status
            { 
                get 
                { 
                    if(Best == null)
                        return "Computing landing trajectory...";
                    return string.Format("Computing landing trajectory.\n" +
                                         "Landing site error: {0}", Utils.formatBigValue((float)Best.DistanceToTarget, "m")); 
                } 
            }

            protected LandingSiteOptimizerBase(LandingTrajectoryAutopilot module, float dtol)
            { 
                this.module = module;
                this.dtol = dtol; 
            }

            public abstract IEnumerator<LandingTrajectory> GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator()
            { return GetEnumerator(); }

            protected double dR2dV(double dR)
            {
                return dR*(10-module.CFG.Target.AngleTo(module.VSL)/Math.PI*9.9)*module.Body.GeeASL;
            }

            protected bool continue_calculation(LandingTrajectory prev, LandingTrajectory cur)
            {
                return Best == null || prev == null || 
                    Best.DistanceToTarget > dtol && 
                    (Math.Abs(cur.DeltaR-prev.DeltaR) > 1e-5 || 
                     Math.Abs(cur.DeltaFi-prev.DeltaFi) > 1e-5);
            }
        }

		public override void Init()
		{
			base.Init();
            CorrectionMaxDist.Value = 5;
			CorrectionTimer.Period = LTRJ.CorrectionTimer;
			StageTimer.action = () => 
			{
				VSL.ActivateNextStage();
				Message("Have to drop ballast to decelerate...");
			};
			dP_up_timer.action = () =>
			{
				dP_threshold = Utils.ClampL(dP_threshold * 0.9, LTRJ.MinDPressure);
				last_dP = VSL.vessel.dynamicPressurekPa;
			};
			dP_down_timer.action = () =>
			{
				dP_threshold = Utils.ClampH(dP_threshold * 1.1, LTRJ.MaxDPressure);
				last_dP = VSL.vessel.dynamicPressurekPa;
			};
			NoEnginesTimer.action = () =>
			{
				landing_stage = LandingStage.HardLanding;
			};
			sim = new AtmoSim(VSL);
			Executor = new ManeuverExecutor(TCA);
            scanner = new PQS_Scanner_CDOS(VSL, GLB.LND.MaxUnevenness/3);
			dP_threshold = LTRJ.MaxDPressure;
			last_Err = 0;
			last_dP = 0;
			Working = false;
            scanned = false;
		}

		protected override void reset()
		{
			base.reset();
			landing_stage = LandingStage.None;
			scanner.Reset();
			DecelerationTimer.Reset();
			dP_up_timer.Reset();
			dP_down_timer.Reset();
			dP_threshold = LTRJ.MaxDPressure;
			last_Err = 0;
			last_dP = 0;
			Working = false;
			scanned = false;
		}

        protected override bool check_target()
        {
            if(!base.check_target()) return false;
            var orb = CFG.Target.GetOrbit();
            if(orb != null && orb.referenceBody != VSL.Body)
            {
                Status("yellow", "Target should be in the same sphere of influence.");
                return false;
            }
            if(CFG.Target.IsProxy)
            {
                if(CFG.Target.IsVessel)
                {
                    if(!TargetVessel.LandedOrSplashed)
                    {
                        Status("yellow", "Target vessel should be landed");
                        return false;
                    }
                }
                Status("yellow", "Target should be a vessel or a waypoint");
                return false;
            }
            return true;
        }

		protected bool landing { get { return landing_stage != LandingStage.None; } }

		protected bool check_initial_trajectory()
		{
            var fuel_needed = trajectory.GetTotalFuel();
            var hover_time = fuel_needed < VSL.Engines.AvailableFuelMass? 
                VSL.Engines.MaxHoverTimeASL(VSL.Engines.AvailableFuelMass-fuel_needed) : 0;
			var status = "";
			var needed_hover_time = LandASAP? LTRJ.HoverTimeThreshold / 5 : LTRJ.HoverTimeThreshold;
			var enough_fuel = hover_time > needed_hover_time || CheatOptions.InfinitePropellant;
			if(trajectory.DistanceToTarget < LTRJ.Dtol && enough_fuel) return true;
			if(!enough_fuel)
			{
                status += string.Format("<b>WARNING</b>: Fuel is <color=magenta><b>{0:P0}</b></color> below safe margin for powered landing.\n", 
                                        (needed_hover_time-hover_time)/needed_hover_time);
				if(Body.atmosphere && VSL.OnPlanetParams.HaveParachutes)
					status += "<i>Landing with parachutes may be possible, " +
						"but you're advised to supervise the process.</i>\n";
			}
			if(trajectory.DistanceToTarget > LTRJ.Dtol)
                status += string.Format("<b>WARNING</b>: Predicted landing site is too far from the target.\n" +
				                        "Error is <color=magenta><b>{0}</b></color>\n", 
				                        Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
            if(trajectory.WillOverheat)
                status += string.Format("<b>WARNING</b>: predicted reentry temperature is <color=magenta><b>{0:F0}K</b></color>\n" +
                                        "<color=red><b>The ship may loose integrity and explode!</b></color>\n", trajectory.MaxShipTemperature);
			status += "\n<color=red><b>Push to proceed. At your own risk.</b></color>";
			Status("yellow", status);
			return false;
		}

		protected void start_landing()
		{
			Working = false;
            flat_target = false;
			clear_nodes();
			update_trajectory();
			VSL.Controls.StopWarp();
			VSL.Controls.Aligned = false;
			CFG.AltitudeAboveTerrain = false;
			landing_stage = LandingStage.Wait;
			PressureASL = Body.GetPressure(0);
		}

		protected void warp_to_coundown()
		{
            if(VSL.Controls.CanWarp)
				VSL.Controls.WarpToTime = VSL.Physics.UT+(VSL.Info.Countdown > 0? 
				                                          Utils.ClampH(VSL.Info.Countdown, 60) : 60);
			else 
                VSL.Controls.StopWarp();
		}

		protected bool correct_trajectory()
		{
			warp_to_coundown();
			if(!CorrectionTimer.TimePassed) return false;
			CorrectionTimer.Reset();
            update_trajectory();
            Log("Correcting? DeltaR {} deg, DeltaFi {} m",
                trajectory.DeltaR, Math.Abs(trajectory.DeltaFi)*Mathf.Deg2Rad*Body.Radius);//debug
            if(trajectory.DeltaR > 0 ||
//               trajectory.BrakeEndDeltaAlt < LTRJ.FlyOverAlt ||
               Math.Abs(trajectory.DeltaFi)*Mathf.Deg2Rad*Body.Radius >= LTRJ.Dtol)
			{ 
				fine_tune_approach(); 
				return false; 
			}
			return !Body.atmosphere;
		}

		protected void add_correction_node_if_needed()
		{
            var nodeDeltaV = trajectory.NodeDeltaV;
            //forbid moving landing site further from the target
            if(CurrentTrajectory.DeltaR < 0)//test it
            {
                nodeDeltaV.x = Math.Min(nodeDeltaV.x, 0);     
                nodeDeltaV.z = Math.Min(nodeDeltaV.z, 0);
            }
            var dV_threshold = Utils.ClampL(LTRJ.CorrectionMinDv * (1-CFG.Target.AngleTo(VSL)/Math.PI), 
                                            GLB.THR.MinDeltaV*2);
            Log("add correction: min dV {}, dV {}, Current Trajectory: {}", 
                dV_threshold, nodeDeltaV, CurrentTrajectory);//debug
            if(nodeDeltaV.magnitude > dV_threshold)
			{
				clear_nodes(); 
                add_node_rel(nodeDeltaV, trajectory.StartUT);
				CFG.AP1.OnIfNot(Autopilot1.Maneuver);
				VSL.Controls.StopWarp();
			}
		}

		double distance_from_ground(Orbit orb, double UT)
		{
			var pos = BodyRotationAtdT(Body, VSL.Physics.UT-UT)*orb.getRelativePositionAtUT(UT);
			return pos.magnitude-VSL.Geometry.D-Body.Radius - Body.TerrainAltitude(pos.xzy+Body.position);
		}

        double obstacle_between(BaseTrajectory trj, double start, double stop, float offset)
        {
            var UT = start;
            var dT = (stop-start);
            double dist = 1;
            while(dT > 0.01)
            {
                var d1p = UT+dT > stop?  double.MaxValue : distance_from_ground(trj.Orbit, UT+dT);
                var d1m = UT-dT < start? double.MaxValue : distance_from_ground(trj.Orbit, UT-dT);
                if(d1p < d1m) { dist = d1p; UT += dT; }
                else { dist = d1m; UT -= dT; }
                if(dist < offset) return offset-dist;
                dT /= 2;
            }
            return offset-dist;
        }

        protected double obstacle_ahead(float offset = 0)
        { 
            if(trajectory != null)
            {
                var dist = obstacle_between(trajectory, trajectory.StartUT, 
                                            Math.Min(trajectory.FlyAboveUT, trajectory.AtTargetUT-0.1), offset);
                if(dist > 0)//debug
                    Log("Obstacle ahead: dist {}, startUT {}, endUT {}, offset {}m", 
                        dist, trajectory.StartUT, Math.Min(trajectory.FlyAboveUT, trajectory.AtTargetUT-0.1), offset);//debug
            }
            return -1;
        }

        IEnumerator<double> obstacle_searcher;
        IEnumerator<double> biggest_obstacle_searcher(LandingTrajectory trj, float offset)
		{
            var start = trj.StartUT;
            var stop = trj.BrakeEndUT;
            var dT = (stop-start)/100;
            var UT0 = start;
            var UT1 = UT0+dT;
            var dist = -1.0;
            while(UT0 < stop)
            {
                Status("white", "Scanning for obstacles: <color=lime>{0:P1}</color>", 
                       Math.Min(1, (UT1-start)/(stop-start)));
                var d = obstacle_between(trj, UT0, UT1, offset);
                UT0 = UT1; UT1 += dT;
                if(d > dist) dist = d;
                yield return dist;
            }
            ClearStatus();
		}

        protected bool find_biggest_obstacle_ahead(float offset, out double obstacle_height)
        {
            obstacle_height = -1;
            if(trajectory == null) return false ;
            if(obstacle_searcher == null)
            {
                obstacle_searcher = biggest_obstacle_searcher(trajectory, offset);
                if(!obstacle_searcher.MoveNext()) return false;
            }
            obstacle_height = obstacle_searcher.Current;
            if(obstacle_searcher.MoveNext()) return true;
            obstacle_searcher = null;
            return false;
        }

		void rel_altitude_if_needed()
		{ CFG.AltitudeAboveTerrain = VSL.Altitude.Relative < 5000; }

		void approach()
		{
			CFG.BR.Off();
			CFG.BlockThrottle = true;
			CFG.AltitudeAboveTerrain = true;
			CFG.VF.On(VFlight.AltitudeControl);
			CFG.DesiredAltitude = LTRJ.ApproachAlt < VSL.Altitude.Relative/2? 
				LTRJ.ApproachAlt : Utils.ClampL(VSL.Altitude.Relative/2, VSL.Geometry.H*2);
			SetTarget(CFG.Target);
			CFG.Nav.On(Navigation.GoToTarget);
			if(CFG.Target.IsVessel) CFG.Target.Radius = 7;
			landing_stage = LandingStage.Approach;
		}

		void decelerate(bool collision_detected)
		{
			VSL.Controls.StopWarp();
			DecelerationTimer.Reset();
			landing_stage = LandingStage.Decelerate; 
			Working = collision_detected;
		}

		void land()
		{
			if(CFG.Target != null && !CFG.Target.IsVessel)
				LND.StartFromTarget();
			CFG.AP1.On(Autopilot1.Land);
			landing_stage = LandingStage.Land;
		}

		void compute_terminal_velocity()
		{
			terminal_velocity = 0;
            if(VSL.VerticalSpeed.Absolute > -100 || VSL.Altitude.Relative < 100+VSL.Geometry.H)
            {
                terminal_velocity = Utils.ClampL(-VSL.VerticalSpeed.Absolute, 0.1f);
                VSL.Info.Countdown = (VSL.Altitude.Relative-VSL.Geometry.H)/terminal_velocity;
            }
            else 
            {
                terminal_velocity = Math.Abs(Vector3d.Dot(trajectory.AtTargetVel, trajectory.AtTargetPos.normalized));
                VSL.Info.Countdown = trajectory.TimeToTarget;
            }
		}

		void setup_for_deceleration()
		{
			CFG.VTOLAssistON = true;
			CFG.AltitudeAboveTerrain = true;
			CFG.AT.OnIfNot(Attitude.Custom);
			ATC.SetThrustDirW(VSL.vessel.srf_velocity);
		}

		protected override void update_trajectory()
		{
			base.update_trajectory();
			VSL.Info.CustomMarkersWP.Add(trajectory.SurfacePoint);
//            Log("current trajectory: {}", trajectory);//debug
		}

		void nose_to_target()
		{
			CFG.BR.OnIfNot(BearingMode.Auto);
			BRC.ForwardDirection = Vector3d.Exclude(VSL.Physics.Up, CFG.Target.WorldPos(Body)-VSL.Physics.wCoM);
		}

		bool correct_attitude_with_thrusters(float turn_time)
		{
			if(VSL.Engines.Active.Steering.Count > 0 &&
			   (VSL.Controls.AttitudeError > Utils.ClampL(1-rel_dP, 0.1f) || 
			    VSL.Torque.NoEngines.MinStopTime() > turn_time)
			   &&
			   (!VSL.Controls.HaveControlAuthority || rel_dP > 0 ||
                VSL.Torque.NoEngines.RotationTime2Phase(VSL.Controls.AttitudeError) > VSL.Info.Countdown))
			{
				THR.Throttle += (float)Utils.ClampH((1+rel_dP) * turn_time/Utils.Clamp(VSL.Info.Countdown, 1, GLB.ATCB.MaxTimeToAlignment), 1);
				return true;
			}
			return false;
		}

		Vector3d correction_direction()
		{
			var t0 = Utils.ClampL(VSL.Info.Countdown, 1e-5);
			var t1 = t0*LTRJ.CorrectionTimeF;
			var TL = trajectory.SurfacePoint.WorldPos(Body)-CFG.Target.WorldPos(Body);
			var correction = -VSL.Physics.Up*VSL.Physics.G*(1-t0*t0/t1/t1) + VSL.vessel.srf_velocity * 2 * ((t1-t0)/t1/t1);
			//overshot lies within [1; 2] interval
			correction += TL.ClampMagnitudeH((float)correction.magnitude) /
				VSL.Engines.MaxAccel*Utils.G0*Body.GeeASL * 
				Utils.ClampH(1 + rel_dP, 2) * 
				Math.Pow(Utils.ClampH(trajectory.DistanceToTarget/LTRJ.Dtol *LTRJ.FlyOverAlt/VSL.Altitude.Relative *
				                      Utils.ClampL(2 + Vector3.Dot(TL.normalized, VSL.HorizontalSpeed.normalized)*LTRJ.CorrectionDirF, 1), 1), 
				         GLB.ANC.DistanceCurve);
            return correction.normalized;
		}

		bool correction_needed;
		bool correct_landing_site()
		{
			ATC.SetThrustDirW(correction_direction());
			var rel_altitude = VSL.Altitude.Relative/LTRJ.FlyOverAlt;
			if(VSL.Controls.HaveControlAuthority && 
			   trajectory.DistanceToTarget > landing_deadzone &&
			   (correction_needed || rel_altitude < 1 || 
			    trajectory.DistanceToTarget > LTRJ.Dtol*rel_altitude))
			{
				THR.Throttle += Utils.ClampH((float)trajectory.DistanceToTarget /
				                             VSL.Engines.MaxAccel/LTRJ.Dtol*LTRJ.CorrectionThrustF, 
				                             VSL.OnPlanetParams.GeeVSF*0.9f);
				correction_needed = trajectory.DistanceToTarget > LTRJ.Dtol;
				return true;
			}
			correction_needed = false;
			return false;
		}

		Vector3d corrected_brake_velocity(Vector3d obt_vel, Vector3d obt_pos)
		{ 
			var vV = Vector3d.Project(obt_vel, obt_pos);
			var vBrake = VSL.Engines.AntigravTTB((float)vV.magnitude);
			var vFactor = 0.5*(Body.atmDensityASL+rel_dP)+vBrake/Utils.ClampL(VSL.Info.Countdown, 0.1f);
			return (obt_vel -
			        vV*(1-Utils.Clamp(vFactor, 0.1, 1)) +
			        Vector3d.Cross(Body.zUpAngularVelocity, obt_pos))
				.xzy;
		}

		Vector3d corrected_brake_direction(Vector3d vel, Vector3d pos)
		{ 
			var tpos = CFG.Target.RelSurfPos(Body);
			return QuaternionD.AngleAxis(Utils.ProjectionAngle(Vector3d.Exclude(pos, vel), 
			                                                   trajectory.SurfacePoint.RelSurfPos(Body)-tpos, 
                                                               Vector3d.Cross(pos, vel)),
			                             VSL.Physics.Up) * vel; 
		}

		void set_destination_vector()
		{ VSL.Info.Destination = CFG.Target.WorldPos(Body)-VSL.Physics.wCoM; }

		void scan_for_landing_site()
		{
            if(scanned) return;
            if(scanner.Idle) 
            {
                scanner.Start(CFG.Target.Pos, LTRJ.PointsPerFrame, 0.01);
                scanner.MaxDist = CorrectionMaxDist.Value * 1000;
            }
			Status("Scanning for <color=yellow><b>flat</b></color> surface to land: <color=lime>{0:P1}</color>", scanner.Progress);
			if(scanner.Scan()) return;
            flat_target = scanner.FlatRegion != null && (!scanner.FlatRegion.Equals(CFG.Target.Pos) || !CFG.Target.IsVessel);
            if(flat_target)
			{
                if(!scanner.FlatRegion.Equals(CFG.Target.Pos))
                {
                    SetTarget(new WayPoint(scanner.FlatRegion));
    				if(trajectory != null) 
    				{
    					trajectory.TargetAltitude = CFG.Target.Pos.Alt;
    					trajectory.Target = CFG.Target;
    				}
                }
                if(scanner.BestUnevennes < GLB.LND.MaxUnevenness)
                    Utils.Message("Found flat region for landing.");
                else 
                    Utils.Message("Moved landing site to a flatter region.");
			}
			scanned = true;
		}

        protected bool scan_for_landing_site_when_in_range()
        {
            if(CorrectTarget && !scanned &&
               Vector3.Angle(VSL.orbit.vel.xzy, -CFG.Target.VectorTo(VSL)) < LTRJ.ScanningAngle)
            {
                VSL.Controls.StopWarp();
                scan_for_landing_site();
                if(scanned) 
                {
                    CFG.AP1.OffIfOn(Autopilot1.Maneuver);
                    clear_nodes();
                    fine_tune_approach();
                }
                Log("scanned {}, scanner {}, flat {}", scanned, scanner, scanner != null? scanner.FlatRegion : null);//debug
                return scanned;
            }
            return false;
        }

        bool can_aerobrake()
        {
            return Body.atmosphere && UseChutes && VSL.OnPlanetParams.HaveParachutes &&
                trajectory.BrakeEndUT > trajectory.AerobrakeStartUT;
        }

		void do_aerobraking_if_requested(bool full = false)
		{
			if(VSL.vessel.staticPressurekPa > 0)
			{
				if(UseBrakes) VSL.BrakesOn();
				if(UseChutes && 
				   VSL.OnPlanetParams.HaveUsableParachutes &&
				   (full || !VSL.OnPlanetParams.ParachutesActive))
					VSL.OnPlanetParams.ActivateParachutesASAP();
			}
		}

		void stop_aerobraking()
		{
			if(UseBrakes) VSL.BrakesOn(false);
			if(UseChutes && VSL.OnPlanetParams.ParachutesActive)
				VSL.OnPlanetParams.CutActiveParachutes();
		}

        void stop_aerobraking_if_needed()
        {
            if(landing_before_target && VSL.vessel.mach < 1)
                stop_aerobraking();
        }

		void brake_with_drag()
		{
            var dir = VSL.OnPlanetParams.MaxAeroForceL;
            if(dir.IsZero()) 
            {
                dir = VSL.Geometry.MaxAreaDirection;
                dir = Mathf.Sign(Vector3.Dot(dir, VSL.vessel.srf_velocity))*dir;
            }
            else dir = -VSL.WorldDir(dir);
			ATC.SetCustomRotationW(dir, VSL.vessel.srf_velocity);
		}

        bool is_overheating()
        {
            return rel_dP > 0 &&
                VSL.vessel.Parts.Any(p => 
                                     p.temperature/p.maxTemp > PhysicsGlobals.TemperatureGaugeThreshold || 
                                     p.skinTemperature/p.skinMaxTemp > PhysicsGlobals.TemperatureGaugeThreshold);
        }

		protected bool do_land()
		{
			if(VSL.LandedOrSplashed) 
			{ 
                #if DEBUG
                if(CFG.Target != null)
                    Log("Distance to target: {}", CFG.Target.DistanceTo(VSL));
                #endif
				stop_aerobraking();
				THR.Throttle = 0; 
				SetTarget();
				ClearStatus(); 
				CFG.AP2.Off(); 
				return true; 
			}
			update_trajectory();
			VSL.Engines.ActivateEngines();
			NoEnginesTimer.RunIf(VSL.Engines.MaxThrustM.Equals(0) && !VSL.Engines.HaveNextStageEngines);
			landing_deadzone = VSL.Geometry.D+CFG.Target.AbsRadius;
			if(VSL.vessel.dynamicPressurekPa > 0)
			{
				if(!dP_up_timer.RunIf(VSL.Controls.AttitudeError > last_Err ||
				                      Mathf.Abs(VSL.Controls.AttitudeError-last_Err) < 0.01f))
					dP_down_timer.RunIf(VSL.Controls.AttitudeError < last_Err &&
					                    VSL.vessel.dynamicPressurekPa < last_dP);
			}
			else dP_threshold = LTRJ.MaxDPressure;
			rel_dP = VSL.vessel.dynamicPressurekPa/dP_threshold;
			last_Err = VSL.Controls.AttitudeError;
			float rel_Ve;
			Vector3d brake_pos, brake_vel, obt_vel;
			vessel_within_range = CFG.Target.DistanceTo(VSL.vessel) < LTRJ.Dtol;
			vessel_after_target = Vector3.Dot(VSL.HorizontalSpeed.Vector, CFG.Target.VectorTo(VSL.vessel)) >= 0;
			target_within_range = trajectory.DistanceToTarget < LTRJ.Dtol;
			landing_before_target = trajectory.DeltaR > 0;
			compute_terminal_velocity();
			switch(landing_stage)
			{
			case LandingStage.Wait:
				Status("Preparing for deceleration...");
				THR.Throttle = 0;
				nose_to_target();
				rel_altitude_if_needed();
				obt_vel = VesselOrbit.getOrbitalVelocityAtUT(trajectory.BrakeStartUT);
				brake_pos = VesselOrbit.getRelativePositionAtUT(trajectory.BrakeStartUT);
				brake_vel = corrected_brake_velocity(obt_vel, brake_pos);
				brake_vel = corrected_brake_direction(brake_vel, brake_pos.xzy);
				CFG.AT.OnIfNot(Attitude.Custom);
				ATC.SetThrustDirW(brake_vel);
                var brake_spd = (float)Math.Min(brake_vel.magnitude, 
                                                VSL.Engines.DeltaV(Math.Max(VSL.Engines.AvailableFuelMass-VSL.Engines.MaxMassFlow*LTRJ.LandingThrustTime, 0)));
                var offset = MatchVelocityAutopilot
                    .BrakingOffset(brake_spd, VSL, out VSL.Info.TTB);
				offset = Mathf.Lerp(VSL.Info.TTB, offset, Utils.Clamp(VSL.Engines.TMR-0.1f, 0, 1));
				VSL.Info.Countdown = trajectory.BrakeEndUT-VSL.Physics.UT-1
					-Math.Max(offset, LTRJ.MinBrakeOffset*(1-Utils.ClampH(Body.atmDensityASL, 1)));
                correct_attitude_with_thrusters(VSL.Torque.MaxPossible.RotationTime2Phase(VSL.Controls.AttitudeError));
                if(obstacle_ahead(0) > 0) 
				{ 
                    decelerate(true); 
                    break; 
                }
                if(VSL.Info.Countdown <= rel_dP ||
                   is_overheating())
                { 
                    decelerate(false); 
                    break; 
                }
                if(VSL.Controls.CanWarp && 
                   (!CorrectTarget || VSL.Info.Countdown > CorrectionOffset))
					VSL.Controls.WarpToTime = VSL.Physics.UT+VSL.Info.Countdown;
				else 
                    VSL.Controls.StopWarp();
                if(CorrectTarget && VSL.Info.Countdown < CorrectionOffset) 
                    scan_for_landing_site();
				break;
			case LandingStage.Decelerate:
				rel_altitude_if_needed();
				CFG.BR.Off();
				if(Working)
				{
					Status("red", "Possible collision detected.");
                    correct_attitude_with_thrusters(VSL.Torque.MaxPossible.RotationTime2Phase(VSL.Controls.AttitudeError));
					Executor.Execute(VSL.Physics.Up*10);
                    if(obstacle_ahead(100) > 0) { CollisionTimer.Reset(); break; }
					if(!CollisionTimer.TimePassed) break;
					start_landing();
					break;
				}
				Status("white", "Decelerating. Landing site error: {0}", 
				       Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
				if(CorrectTarget) 
					scan_for_landing_site();
				do_aerobraking_if_requested();
                var overheating = is_overheating();
                if(!overheating && VSL.Engines.AvailableFuelMass/VSL.Engines.MaxMassFlow < LTRJ.LandingThrustTime)
                {
                    Message(10, "Not enough fuel for powered landing.\nPerforming emergency landing...");
                    landing_stage = LandingStage.HardLanding;
                    break;
                }
                if(VSL.Controls.HaveControlAuthority) 
                    DecelerationTimer.Reset();
                if(vessel_after_target)
				{ 
                    if(Executor.Execute(-VSL.vessel.srf_velocity, LTRJ.BrakeEndSpeed)) 
                        break; 
                }
                else if(overheating ||
                        !landing_before_target && 
                        !DecelerationTimer.TimePassed &&
                        trajectory.DistanceToTarget > landing_deadzone)
				{ 
					THR.Throttle = 0;
					VSL.Info.TTB = VSL.Engines.TTB((float)VSL.vessel.srfSpeed);
                    CFG.AT.OnIfNot(Attitude.Custom);
					var aerobraking = rel_dP > 0 && VSL.OnPlanetParams.ParachutesActive;
                    if(overheating) 
                    {
                        ATC.SetThrustDirW(VSL.vessel.srf_velocity);
                        THR.Throttle = 1;
                    }
                    else
					{
                        brake_vel = corrected_brake_velocity(VesselOrbit.vel, VesselOrbit.pos);
                        brake_vel = corrected_brake_direction(brake_vel, VesselOrbit.pos.xzy);
                        ATC.SetThrustDirW(brake_vel);
						THR.Throttle = CFG.Target.DistanceTo(VSL.vessel) > trajectory.DistanceToTarget?
                            (float)Utils.ClampH(trajectory.DistanceToTarget/landing_deadzone/3
                                                /(1+Vector3.Dot(brake_vel.normalized, VSL.Physics.Up)), 1) : 1;
					}
					if(THR.Throttle > 0 || aerobraking) break;
				}
				landing_stage = LandingStage.Coast;
				break;
			case LandingStage.Coast:
				Status("white", "Coasting. Landing site error: {0}", Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
                if(is_overheating())
                {
                    Message(10, "The ship is overheating!\nPerforming emergency landing...");
                    landing_stage = LandingStage.HardLanding;
                    break;
                }
				THR.Throttle = 0;
				nose_to_target();
				setup_for_deceleration();
                if(correct_landing_site())
                    correct_attitude_with_thrusters(VSL.Torque.MaxPossible.RotationTime2Phase(VSL.Controls.AttitudeError));
				VSL.Info.TTB = VSL.Engines.TTB((float)VSL.vessel.srfSpeed);
				VSL.Info.Countdown -= Math.Max(VSL.Info.TTB+VSL.Torque.NoEngines.TurnTime+VSL.vessel.dynamicPressurekPa, ManeuverOffset);
				if(VSL.Info.Countdown > 0)
				{ 
                    if(THR.Throttle.Equals(0)) 
                        warp_to_coundown(); 
                }
				else
				{
					Working = false;
					rel_Ve = VSL.Engines.RelVeASL;
					if(rel_Ve <= 0)
					{
						Message(10, "Not enough thrust for powered landing.\nPerforming emergency landing...");
						landing_stage = LandingStage.HardLanding;
						break;
					}
					if(!(VSL.Controls.HaveControlAuthority || VSL.Torque.HavePotentialControlAuthority))
					{
						Message(10, "Lacking control authority to land properly.\nPerforming emergency landing...");
						landing_stage = LandingStage.HardLanding;
						break;
					}
					var fuel_left = VSL.Engines.AvailableFuelMass;
					var fuel_needed = VSL.Engines.FuelNeeded((float)terminal_velocity, rel_Ve);
					var needed_hover_time = LandASAP? LTRJ.HoverTimeThreshold / 5 : LTRJ.HoverTimeThreshold;
					if(!CheatOptions.InfinitePropellant && 
					   (fuel_needed >= fuel_left ||
					    VSL.Engines.MaxHoverTimeASL(fuel_left-fuel_needed) < needed_hover_time))
					{
						Message(10, "Not enough fuel for powered landing.\nPerforming emergency landing...");
						landing_stage = LandingStage.HardLanding;
						break;
					}
					landing_stage = LandingStage.SoftLanding;
				}
				break;
			case LandingStage.HardLanding:
                var status = VSL.OnPlanetParams.ParachutesActive? 
                    "<color=yellow><b>Landing on parachutes.</b></color>" : 
                    "<color=yellow><b>Emergency Landing.</b></color>";
                status += string.Format("\nVertical impact speed: <color=red><b>{0}</b></color>", 
                                        Utils.formatBigValue((float)terminal_velocity, "m/s"));
				set_destination_vector();
				CFG.BR.Off();
				var not_too_hot = VSL.vessel.externalTemperature < VSL.Physics.MinMaxTemperature;
				if(not_too_hot) setup_for_deceleration();
                if(VSL.Engines.MaxThrustM > 0 && terminal_velocity > 4 &&
				   (VSL.Controls.HaveControlAuthority || VSL.Torque.HavePotentialControlAuthority))
				{
                    VSL.Info.TTB = VSL.Engines.OnPlanetTTB(VSL.vessel.srf_velocity, VSL.Physics.Up, VSL.Altitude.Absolute);
                    VSL.Info.Countdown -= VSL.Info.TTB;
					if((VSL.Info.Countdown < 0 && 
					    (!VSL.OnPlanetParams.HaveParachutes || 
					     VSL.OnPlanetParams.ParachutesActive && VSL.OnPlanetParams.ParachutesDeployed)))
                        Working = true;
                    else if(VSL.Info.Countdown > 0.5f)
                    {
                        Working = false;
                        THR.Throttle = 0;
                    }
                    if(Working)
                    {
                        THR.CorrectThrottle = false;
                        THR.Throttle = VSL.VerticalSpeed.Absolute < -5? 1 : VSL.OnPlanetParams.GeeVSF;
                    }
                    status += "\nWill deceletate as much as possible before impact.";
				}
				if(Body.atmosphere && VSL.OnPlanetParams.HaveUsableParachutes)
				{
                    if(vessel_within_range || vessel_after_target || !landing_before_target)
//                       trajectory.BrakeEndUT < trajectory.AerobrakeStartUT ||
//                       trajectory.BrakeEndUT-VSL.Physics.UT < LTRJ.ParachutesDeployOffset)
						VSL.OnPlanetParams.ActivateParachutesASAP();
					else 
						VSL.OnPlanetParams.ActivateParachutesBeforeUnsafe();
					if(!VSL.OnPlanetParams.ParachutesActive)
					{
						//don't push our luck when it's too hot outside
						if(not_too_hot) brake_with_drag();
						else
						{
							CFG.AT.Off();
							CFG.StabilizeFlight = false;
							VSL.vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
						}
						StageTimer.RunIf(Body.atmosphere && //!VSL.Controls.HaveControlAuthority &&
						                 VSL.vessel.currentStage-1 > VSL.OnPlanetParams.NearestParachuteStage &&
						                 VSL.vessel.dynamicPressurekPa > LTRJ.DropBallastThreshold*PressureASL && 
						                 VSL.vessel.mach > LTRJ.MachThreshold);
						if(CFG.AutoParachutes) 
                            status += "\nWaiting for the right moment to deploy parachutes.";
						else 
                            status += "\n<color=red>Automatic parachute deployment is disabled." +
                                "\nActivate parachutes manually when needed.</color>";
					}
				}
                if(Body.atmosphere)
                    VSL.BrakesOn();
				if(!VSL.OnPlanetParams.HaveParachutes && 
				   !VSL.Engines.HaveNextStageEngines && 
				   (VSL.Engines.MaxThrustM.Equals(0) || !VSL.Controls.HaveControlAuthority))
				{
					if(Body.atmosphere && not_too_hot) brake_with_drag();
                    status += "\n<color=red><b>Crash is imminent!</b></color>";
				}
                Status(status);
				break;
			case LandingStage.SoftLanding:
				CFG.BR.Off();
				THR.Throttle = 0;
				set_destination_vector();
				setup_for_deceleration();
                if(vessel_within_range || vessel_after_target)// ||
//				   trajectory.BrakeEndUT-VSL.Physics.UT < LTRJ.ParachutesDeployOffset) 
					do_aerobraking_if_requested(true);
                var turn_time = VSL.Torque.MaxPossible.RotationTime2Phase(VSL.Controls.AttitudeError);
                var CPS_Correction = CPS.CourseCorrection;
                if(!CPS_Correction.IsZero())
                {
                    Status("red", "Avoiding collision!");
                    CFG.Target = trajectory.SurfacePoint;
                    trajectory.Target = CFG.Target;
                    trajectory.TargetAltitude = CFG.Target.Pos.Alt;
                    ATC.SetThrustDirW(CPS_Correction-VSL.vessel.srf_velocity);
                    THR.DeltaV = CPS_Correction.magnitude+(float)VSL.vessel.srfSpeed;
                    THR.CorrectThrottle = false;
                    flat_target = false;
                    break;
                }
				if(!Working)
				{
					correct_landing_site();
					correct_attitude_with_thrusters(turn_time);
                    VSL.Info.TTB = VSL.Engines.OnPlanetTTB(VSL.vessel.srf_velocity, VSL.Physics.Up, VSL.Altitude.Absolute);
					VSL.Info.Countdown -= VSL.Info.TTB+turn_time;
					Working = VSL.Info.Countdown <= 0 || VSL.vessel.srfSpeed < LTRJ.BrakeEndSpeed;
					if(!Working)
					{
						if(VSL.Controls.InvAlignmentFactor > 0.5) 
							Status("white", "Final deceleration: correcting attitude.\nLanding site error: {0}", 
							       Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
						else 
							Status("white", "Final deceleration: waiting for the burn.\nLanding site error: {0}", 
							       Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
						break;
					}
				}
				if(Working)
				{
					ATC.SetThrustDirW(correction_direction());
					if(!VSL.Controls.HaveControlAuthority) 
					{ 
						correct_attitude_with_thrusters(turn_time);
						if(!VSL.Torque.HavePotentialControlAuthority) 
							landing_stage = LandingStage.HardLanding;
						break; 
					}
                    THR.CorrectThrottle = false;
                    if(target_within_range && (flat_target || VSL.Altitude.Relative > GLB.LND.WideCheckAltitude))
					{
                        if(VSL.VerticalSpeed.Absolute < 0)
                            THR.Throttle = VSL.Info.Countdown < 0.1f? 1 :
                                Utils.Clamp(-VSL.VerticalSpeed.Absolute/(VSL.Engines.MaxAccel-VSL.Physics.G)/
                                            Utils.ClampL((float)VSL.Info.Countdown, 0.01f), 
                                            VSL.OnPlanetParams.GeeVSF*1.1f, 1);
                        else
                            THR.Throttle = Utils.ClampH(VSL.HorizontalSpeed.Absolute/LTRJ.BrakeThrustThreshod *
                                                        VSL.Controls.AlignmentFactor, 1);
					}
					else THR.Throttle = 1;
                    if(VSL.Altitude.Relative > GLB.LND.StopAtH*VSL.Geometry.D &&
                       VSL.VerticalSpeed.Absolute < 0)
					{
						VSL.Info.TTB = VSL.Engines.OnPlanetTTB(VSL.vessel.srf_velocity, VSL.Physics.Up, VSL.Altitude.Absolute);
						VSL.Info.Countdown -= VSL.Info.TTB+turn_time;
						Working = THR.Throttle > 0.7 || VSL.Info.Countdown < 10;
						Status("white", "Final deceleration. Landing site error: {0}", 
						       Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
						break;
					}
				}
				THR.Throttle = 0;
				if(LandASAP) 
                    landing_stage = LandingStage.LandHere;
				else
				{
					stop_aerobraking();
					if(CFG.Target.DistanceTo(VSL.vessel)-VSL.Geometry.R > LTRJ.Dtol) 
                        approach();
					else 
                        land();
				}
				break;
			case LandingStage.LandHere:
				Status("lime", "Landing...");
				CFG.BR.Off();
				CFG.BlockThrottle = true;
				CFG.AltitudeAboveTerrain = true;
				CFG.VF.On(VFlight.AltitudeControl);
				CFG.HF.OnIfNot(HFlight.Stop);
				if(CFG.DesiredAltitude >= 0 && !VSL.HorizontalSpeed.MoovingFast)
					CFG.DesiredAltitude = 0;
				else CFG.DesiredAltitude = Utils.ClampL(VSL.Altitude.Relative/2, VSL.Geometry.H*2);
				break;
			case LandingStage.Approach:
				Status("Approaching the target...");
				set_destination_vector();
				if(!CFG.Nav[Navigation.GoToTarget]) land();
				break;
			case LandingStage.Land: 
				set_destination_vector();
				break;
			}
			return false;
		}

		public void DrawDeorbitSettings()
		{
            GUILayout.BeginVertical();
			GUILayout.BeginHorizontal();
			Utils.ButtonSwitch("Use Brakes", ref UseBrakes, "Use brakes during deceleration.");
			if(Body.atmosphere && VSL.OnPlanetParams.HaveParachutes)
				Utils.ButtonSwitch("Use Parachutes", ref UseChutes, "Use parachutes during deceleration.");
			else GUILayout.Label("Use Parachutes", Styles.grey_button);
			Utils.ButtonSwitch("Correct Target", ref CorrectTarget, 
			                   "Search for a flat surface before deceleration and correct the target site.");
			Utils.ButtonSwitch("Land ASAP", ref LandASAP, 
			                   "Do not try to Go To the target if missed or to search for a landing site near the surface.");
			GUILayout.EndHorizontal();
            if(CorrectTarget)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Max. Correction:", 
                                               "Maximum distance of a corrected landing site from the original one"));
                CorrectionMaxDist.Draw("km", 1f, suffix_width: 25);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            #if DEBUG
            DrawAtmoTrajectory();
            #endif
		}

		#if DEBUG
		void log_flight()
		{
			var v = VSL.vessel;
			CSV(
				VSL.Altitude.Absolute,
				v.staticPressurekPa,
				v.atmDensity,
				v.atmDensity/Body.atmDensityASL,
				v.atmosphericTemperature,
				VSL.Physics.G,
				v.srfSpeed,
				VSL.HorizontalSpeed.Absolute,
				Mathf.Abs(VSL.VerticalSpeed.Absolute),
				v.mach,
				v.dynamicPressurekPa,
				VSL.Controls.AttitudeError
			);
		}

		protected virtual void DrawDebugLines()
		{
			if(IsActive)
			{
				Utils.GLVec(VSL.refT.position, VSL.vessel.srf_velocity, Color.yellow);
				if(CFG.Target != null)
					Utils.GLLine(VSL.refT.position, CFG.Target.WorldPos(Body), Color.magenta);
			}
		}

        public void DrawAtmoTrajectory()
        {
            var t = trajectory ?? current_landing_trajectory;
            if(MapView.MapIsEnabled && t != null)
            {
                if(t.AtmoTrajectory != null &&
                   t.AtmoTrajectory.Points.Count > 1)
                {
                    Utils.GLLines(t
                                  .AtmoTrajectory
                                  .ToCBFramePath(VSL.Physics.UT),
                                  Color.magenta);
                }
                if(t.AfterBrakeTrajectory != null && 
                   t.AfterBrakeTrajectory.Points.Count > 1)
                    Utils.GLLines(t
                                  .AfterBrakeTrajectory
                                  .ToCBFramePath(VSL.Physics.UT),
                                  Color.green);
//                if(t.BrakePoint.UT > 0)
//                    VSL.Info.CustomMarkersVec.Add((TrajectoryCalculator.
//                                                   BodyRotationAtdT(Body, VSL.Physics.UT-t.BrakePoint.UT)*
//                                                   t.BrakePoint.pos).xzy);
            }
        }
		#endif
	}

    public abstract class PQS_Scanner
    {
        protected readonly VesselWrapper VSL;

        protected Coordinates start;
        protected int points_per_frame;
        protected double delta, half;

        public double Delta { get { return delta*Mathf.Deg2Rad*VSL.Body.Radius; } }

        public double MaxDist = -1;
        public double MaxUnevennes;
        public double BestUnevennes { get; protected set; }
        public Coordinates FlatRegion { get; protected set; }
        public abstract bool Idle { get; }
        public abstract float Progress { get; }

        protected PQS_Scanner(VesselWrapper vsl, double max_unevenness) 
        { 
            VSL = vsl; 
            MaxUnevennes = max_unevenness;
        }

        public virtual void Reset()
        {
            FlatRegion = null;
            BestUnevennes = double.MaxValue;
        }

        protected void Start(Coordinates start, int points_per_frame)
        {
            Reset();
            this.start = start.Copy();
            this.points_per_frame = points_per_frame;
            delta = VSL.Geometry.D/VSL.Body.Radius*Mathf.Rad2Deg;
            half = delta/2;
        }

        protected double altitude_delta(double lat, double lon, double prev_alt)
        { return Math.Abs(new Coordinates(lat, lon, 0).SurfaceAlt(VSL.Body, true)-prev_alt); }

        protected double calculate_unevenness(double lat, double lon)
        {
            var current_point = Coordinates.SurfacePoint(lat, lon, VSL.Body);
            #if DEBUG
            VSL.Info.AddCustopWaypoint(current_point, "Checking...");
            #endif
            if(current_point.OnWater) 
                return double.PositiveInfinity;
            else
            {
                var alt_delta = altitude_delta(current_point.Lat-half, current_point.Lon-half, current_point.Alt);
                alt_delta += altitude_delta(current_point.Lat+half, current_point.Lon-half, current_point.Alt);
                alt_delta += altitude_delta(current_point.Lat+half, current_point.Lon+half, current_point.Alt);
                alt_delta += altitude_delta(current_point.Lat-half, current_point.Lon+half, current_point.Alt);
                return alt_delta/VSL.Geometry.D;
            }
        }

        protected bool good_point(double lat, double lon, double unevenness)
        {
            return MaxDist < 0 || start.DistanceTo(new Coordinates(lat, lon, 0), VSL.Body) < MaxDist;
        }

        public abstract bool Scan();
    }

    public class PQS_Scanner_CDOS : PQS_Scanner
    {
        CDOS_Optimizer2D_Generic optimizer;
        IEnumerator optimization;

        public override bool Idle 
        { get { return optimizer == null; } }

        public override float Progress
        { get { return optimizer == null? 0 : (float)Math.Min(MaxUnevennes/optimizer.BestValue, 1); } }

        public PQS_Scanner_CDOS(VesselWrapper vsl, double max_unevenness) 
            : base(vsl, max_unevenness) {}

        public void Start(Coordinates pos, int num_points_per_frame, double tol)
        {
            base.Start(pos, num_points_per_frame);
            optimizer = new CDOS_Optimizer2D_Generic(pos.Lat, pos.Lon, delta*10, tol*delta, 1e-7, calculate_unevenness, good_point);
        }

        public override void Reset()
        {
            base.Reset();
            optimizer = null;
            optimization = null;
        }

        public override bool Scan()
        {
            if(optimizer == null) return false;
            if(optimization == null)
                optimization = optimizer.GetEnumerator();
            for(var p = 0; p < points_per_frame; p++)
            {
                if(!optimization.MoveNext())
                {
                    var best = optimizer.Best;
                    FlatRegion = Coordinates.SurfacePoint(best.x, best.y, VSL.Body);
                    BestUnevennes = best.z;
                    Utils.Log("Best: {} < {}", BestUnevennes, MaxUnevennes);//debug
                    return false;
                }
            }
            return true;
        }
    }
}

