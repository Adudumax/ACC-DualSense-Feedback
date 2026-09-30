using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace ACCDualSenseFeedback.Telemetry;

internal unsafe sealed class AccSharedMemoryReader : IDisposable
{
    internal const int PhysicsPageSize = 800;
    private static readonly string[] MapNames = [@"Local\acpmf_physics", "acpmf_physics"];

    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private byte* _pointer;
    private bool _pointerAcquired;

    public bool IsConnected => _pointer != null;

    public bool TryConnect()
    {
        if (IsConnected)
            return true;

        foreach (string name in MapNames)
        {
            try
            {
                var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                var view = mapping.CreateViewAccessor(0, PhysicsPageSize, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                pointer += view.PointerOffset;

                _mapping = mapping;
                _view = view;
                _pointer = pointer;
                _pointerAcquired = true;
                return true;
            }
            catch (FileNotFoundException)
            {
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    public bool TryReadLatest(out TelemetrySnapshot snapshot)
    {
        snapshot = default;
        if (_pointer == null)
            return false;

        AccPhysicsPage* page = (AccPhysicsPage*)_pointer;
        int firstPacket = page->PacketId;
        Thread.MemoryBarrier();

        snapshot.PacketId = firstPacket;
        snapshot.Gas = page->Gas;
        snapshot.Brake = page->Brake;
        snapshot.Gear = page->Gear;
        snapshot.Rpm = page->Rpm;
        snapshot.CurrentMaxRpm = page->CurrentMaxRpm;
        snapshot.IsEngineRunning = page->IsEngineRunning;
        snapshot.SpeedKmh = page->SpeedKmh;
        snapshot.SteerAngle = page->SteerAngle;
        snapshot.Pitch = page->Pitch;
        snapshot.Roll = page->Roll;
        snapshot.GForceX = page->AccG[0];
        snapshot.GForceY = page->AccG[1];
        snapshot.GForceZ = page->AccG[2];
        snapshot.LocalVelocityX = page->LocalVelocity[0];
        snapshot.LocalVelocityY = page->LocalVelocity[1];
        snapshot.LocalVelocityZ = page->LocalVelocity[2];
        snapshot.LocalAngularVelocityX = page->LocalAngularVelocity[0];
        snapshot.LocalAngularVelocityY = page->LocalAngularVelocity[1];
        snapshot.LocalAngularVelocityZ = page->LocalAngularVelocity[2];
        // Tc/Abs are the configured assist levels, not live intervention.
        // Only the *InAction fields are suitable as event signals.
        snapshot.TcLevel = page->Tc;
        snapshot.AbsLevel = page->Abs;
        snapshot.Tc = page->TcInAction != 0 ? 1f : 0f;
        snapshot.Abs = page->AbsInAction != 0 ? 1f : 0f;
        snapshot.LocalYawRate = snapshot.LocalAngularVelocityY;
        snapshot.FinalFf = page->FinalFf;
        snapshot.KerbVibration = page->KerbVibration;
        snapshot.SlipVibration = page->SlipVibration;
        snapshot.GVibration = page->GVibration;
        snapshot.AbsVibration = page->AbsVibration;

        snapshot.WheelSlipFl = page->WheelSlip[0];
        snapshot.WheelSlipFr = page->WheelSlip[1];
        snapshot.WheelSlipRl = page->WheelSlip[2];
        snapshot.WheelSlipRr = page->WheelSlip[3];
        snapshot.WheelLoadFl = page->WheelLoad[0];
        snapshot.WheelLoadFr = page->WheelLoad[1];
        snapshot.WheelLoadRl = page->WheelLoad[2];
        snapshot.WheelLoadRr = page->WheelLoad[3];
        snapshot.WheelAngularSpeedFl = page->WheelAngularSpeed[0];
        snapshot.WheelAngularSpeedFr = page->WheelAngularSpeed[1];
        snapshot.WheelAngularSpeedRl = page->WheelAngularSpeed[2];
        snapshot.WheelAngularSpeedRr = page->WheelAngularSpeed[3];
        snapshot.TyreDirtyFl = page->TyreDirtyLevel[0];
        snapshot.TyreDirtyFr = page->TyreDirtyLevel[1];
        snapshot.TyreDirtyRl = page->TyreDirtyLevel[2];
        snapshot.TyreDirtyRr = page->TyreDirtyLevel[3];
        snapshot.NumberOfTyresOut = page->NumberOfTyresOut;
        snapshot.SlipRatioFl = page->SlipRatio[0];
        snapshot.SlipRatioFr = page->SlipRatio[1];
        snapshot.SlipRatioRl = page->SlipRatio[2];
        snapshot.SlipRatioRr = page->SlipRatio[3];
        snapshot.SlipAngleFl = page->SlipAngle[0];
        snapshot.SlipAngleFr = page->SlipAngle[1];
        snapshot.SlipAngleRl = page->SlipAngle[2];
        snapshot.SlipAngleRr = page->SlipAngle[3];
        snapshot.SuspensionFl = page->SuspensionTravel[0];
        snapshot.SuspensionFr = page->SuspensionTravel[1];
        snapshot.SuspensionRl = page->SuspensionTravel[2];
        snapshot.SuspensionRr = page->SuspensionTravel[3];
        snapshot.RideHeightFront = page->RideHeight[0];
        snapshot.RideHeightRear = page->RideHeight[1];
        snapshot.FxFl = page->Fx[0];
        snapshot.FxFr = page->Fx[1];
        snapshot.FxRl = page->Fx[2];
        snapshot.FxRr = page->Fx[3];
        snapshot.MzFl = page->Mz[0];
        snapshot.MzFr = page->Mz[1];
        snapshot.MzRl = page->Mz[2];
        snapshot.MzRr = page->Mz[3];
        snapshot.FyFl = page->Fy[0];
        snapshot.FyFr = page->Fy[1];
        snapshot.FyRl = page->Fy[2];
        snapshot.FyRr = page->Fy[3];
        snapshot.BrakePressureFl = page->BrakePressure[0];
        snapshot.BrakePressureFr = page->BrakePressure[1];
        snapshot.BrakePressureRl = page->BrakePressure[2];
        snapshot.BrakePressureRr = page->BrakePressure[3];
        snapshot.SuspensionDamageFl = page->SuspensionDamage[0];
        snapshot.SuspensionDamageFr = page->SuspensionDamage[1];
        snapshot.SuspensionDamageRl = page->SuspensionDamage[2];
        snapshot.SuspensionDamageRr = page->SuspensionDamage[3];
        snapshot.CarDamageFront = page->CarDamage[0];
        snapshot.CarDamageRear = page->CarDamage[1];
        snapshot.CarDamageLeft = page->CarDamage[2];
        snapshot.CarDamageRight = page->CarDamage[3];
        snapshot.CarDamageCenter = page->CarDamage[4];
        snapshot.ContactNormalFlX = page->TyreContactNormal[0];
        snapshot.ContactNormalFlY = page->TyreContactNormal[1];
        snapshot.ContactNormalFlZ = page->TyreContactNormal[2];
        snapshot.ContactNormalFrX = page->TyreContactNormal[3];
        snapshot.ContactNormalFrY = page->TyreContactNormal[4];
        snapshot.ContactNormalFrZ = page->TyreContactNormal[5];
        snapshot.ContactNormalRlX = page->TyreContactNormal[6];
        snapshot.ContactNormalRlY = page->TyreContactNormal[7];
        snapshot.ContactNormalRlZ = page->TyreContactNormal[8];
        snapshot.ContactNormalRrX = page->TyreContactNormal[9];
        snapshot.ContactNormalRrY = page->TyreContactNormal[10];
        snapshot.ContactNormalRrZ = page->TyreContactNormal[11];

        Thread.MemoryBarrier();
        int secondPacket = page->PacketId;
        if (firstPacket != secondPacket)
        {
            snapshot = default;
            return false;
        }

        snapshot.ObservedTimestamp = Stopwatch.GetTimestamp();
        return true;
    }

    public void Disconnect()
    {
        if (_pointerAcquired && _view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointerAcquired = false;
        }

        _pointer = null;
        _view?.Dispose();
        _mapping?.Dispose();
        _view = null;
        _mapping = null;
    }

    public void Dispose() => Disconnect();
}
