// MapComponent_PowerBroker.cs
// Copyright Karel Kroeze, 2020-2020

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using RimWorld;
using UnityEngine;
using Verse;

namespace BackupPower
{
    public class MapComponent_PowerBroker : MapComponent
    {
        public HashSet<Building_BackupPowerAttachment> brokers = new HashSet<Building_BackupPowerAttachment>();

        public MapComponent_PowerBroker( Map map ) : base( map )
        {
        }

        public static void DeregisterBroker( [NotNull] Building_BackupPowerAttachment broker )
        {
            For( broker.Map ).brokers.RemoveSafe( broker );
        }

        public static MapComponent_PowerBroker For( [NotNull] Map map )
        {
            return map?.GetComponent<MapComponent_PowerBroker>();
        }

        public static void RegisterBroker( [NotNull] Building_BackupPowerAttachment broker, bool update = false )
        {
            var comp = For( broker.Map );
            if ( update ) comp.brokers.Remove( broker );
            comp.brokers.AddSafe( broker );
        }

        public float Consumption( CompPowerTrader comp )
        {
            if ( !comp.PowerOn && !FlickUtility.WantsToBeOn( comp.parent ) )
                return 0;

            return Mathf.Max( -comp.PowerOutput, 0f );
        }

        public float CurrentProduction( CompPowerTrader comp )
        {
            if ( !( comp is CompPowerPlant plant ) )
                return 0;

            if ( !plant.PowerOn )
                return 0;

            return Mathf.Max( plant.PowerOutput, 0 );
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if ( Find.TickManager.TicksGame % BackupPower.Settings.UpdateInterval != 0 )
                return;

            foreach ( var group in brokers.Where( b => b.PowerNet != null )
                                          .GroupBy( b => b.PowerNet ) )
                PowerNetUpdate( group.Key, new HashSet<Building_BackupPowerAttachment>( group ) );
        }

        public float PotentialProduction( CompPowerTrader comp )
        {
#if DEBUG
            var _plant         = comp as CompPowerPlant;
            var _refuelable    = _plant?.parent.RefuelableComp();
            var _breakdownable = _plant?.parent.BreakdownableComp();

            var msg = comp.parent.def.defName;
            msg += $"\n\tpowerplant: {_plant != null}";
            if ( _plant != null )
            {
                msg += $"\n\trefuelable: {_refuelable != null}";
                msg += $"\n\tfueled: {_refuelable?.HasFuel}";
                msg += $"\n\tbreakdownable: {_breakdownable != null}";
                msg += $"\n\tbroken: {_breakdownable?.BrokenDown}";
                msg += $"\n\tdesired: {_plant.DesiredOutput()}";
                msg += $"\n\tcurrent: {_plant.PowerOutput}";
            }

            Log.Message( msg );
#endif

        if ( !( comp is CompPowerPlant plant ) )
                return 0;

            var refuelable = plant.parent.RefuelableComp();
            if ( refuelable != null && !refuelable.HasFuel )
                return 0;

            var breakdownable = plant.parent.BreakdownableComp();
            if ( breakdownable != null && breakdownable.BrokenDown )
                return 0;

            // TODO: check how this interacts with variable power output buildings, e.g. solar, wind.
            return Mathf.Max( plant.DesiredOutput(), plant.PowerOutput, 0 );
        }

        public void PowerNetUpdate( PowerNet net, HashSet<Building_BackupPowerAttachment> brokers )
        {
            // get desired power
            var users = net.powerComps
                           .Select( p => ( comp: p,
                                           broker: p.parent is Building building
                                               ? brokers.FirstOrDefault( b => b.Parent == building )
                                               : null,
                                           consumption: Consumption( p ),
                                           currentProduction: CurrentProduction( p ),
                                           potentialProduction: PotentialProduction( p ) ) );

            var need         = users.Sum( u => u.consumption );
            var production   = users.Sum( u => u.currentProduction );
            var hasStorage   = net.HasStorage();
            var storageLevel = net.StorageLevel();

            // Log.Debug( $"need: {need}, production: {production}, static: {staticProduction}" );

            if ( production > need || hasStorage && storageLevel > 0 )
            {
                // try to shut backups off
                var backups = users.Where( u => u.broker            != null
                                             && u.currentProduction > 0
                                             && ( u.currentProduction <= ( production - need ) || u.broker.runOnBatteriesOnly )
                                             && ( ( !hasStorage && !u.broker.runOnBatteriesOnly ) || storageLevel >= u.broker.batteryRange.max )
                                             && u.broker.CanTurnOff() )
                                   .ToList();

                if ( backups.TryRandomElementByWeight( c => 1 / c.currentProduction, out var backup ) )
                    backup.broker.TurnOff();
            }

            if ( production < need || hasStorage && storageLevel < 1 )
            {
                // try to turn backups on
                var backups = users.Where( u => u.broker                        != null
                                             && Math.Abs( u.currentProduction ) < Mathf.Epsilon
                                             && u.potentialProduction           > 0
                                             && ( !hasStorage || storageLevel <= u.broker.batteryRange.min ) )
                                   .ToList();

                if ( backups.TryRandomElementByWeight( c => c.potentialProduction, out var backup ) )
                    backup.broker.TurnOn();
            }
        }
    }
}