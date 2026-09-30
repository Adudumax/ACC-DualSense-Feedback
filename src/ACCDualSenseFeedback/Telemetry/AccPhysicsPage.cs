using System.Runtime.InteropServices;

namespace ACCDualSenseFeedback.Telemetry;

// ACC Shared Memory Documentation v1.8.12. All fields are 4-byte packed.
// This full layout is intentionally retained up to the four ACC-native
// vibration channels so offsets remain auditable and version-safe.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct AccPhysicsPage
{
    public int PacketId;
    public float Gas;
    public float Brake;
    public float Fuel;
    public int Gear;
    public int Rpm;
    public float SteerAngle;
    public float SpeedKmh;
    public fixed float Velocity[3];
    public fixed float AccG[3];
    public fixed float WheelSlip[4];
    public fixed float WheelLoad[4];
    public fixed float WheelsPressure[4];
    public fixed float WheelAngularSpeed[4];
    public fixed float TyreWear[4];
    public fixed float TyreDirtyLevel[4];
    public fixed float TyreCoreTemperature[4];
    public fixed float CamberRad[4];
    public fixed float SuspensionTravel[4];
    public int Drs;
    public float Tc;
    public float Heading;
    public float Pitch;
    public float Roll;
    public float CgHeight;
    public fixed float CarDamage[5];
    public int NumberOfTyresOut;
    public int PitLimiterOn;
    public float Abs;
    public float KersCharge;
    public float KersInput;
    public int AutoShifterOn;
    public fixed float RideHeight[2];
    public float TurboBoost;
    public float Ballast;
    public float AirDensity;
    public float AirTemp;
    public float RoadTemp;
    public fixed float LocalAngularVelocity[3];
    public float FinalFf;
    public float PerformanceMeter;
    public int EngineBrake;
    public int ErsRecoveryLevel;
    public int ErsPowerLevel;
    public int ErsHeatCharging;
    public int ErsIsCharging;
    public float KersCurrentKj;
    public int DrsAvailable;
    public int DrsEnabled;
    public fixed float BrakeTemperature[4];
    public float Clutch;
    public fixed float TyreTempI[4];
    public fixed float TyreTempM[4];
    public fixed float TyreTempO[4];
    public int IsAiControlled;
    public fixed float TyreContactPoint[12];
    public fixed float TyreContactNormal[12];
    public fixed float TyreContactHeading[12];
    public float BrakeBias;
    public fixed float LocalVelocity[3];
    public int P2PActivation;
    public int P2PStatus;
    public int CurrentMaxRpm;
    public fixed float Mz[4];
    public fixed float Fx[4];
    public fixed float Fy[4];
    public fixed float SlipRatio[4];
    public fixed float SlipAngle[4];
    public int TcInAction;
    public int AbsInAction;
    public fixed float SuspensionDamage[4];
    public fixed float TyreTemp[4];
    public float WaterTemp;
    public fixed float BrakePressure[4];
    public int FrontBrakeCompound;
    public int RearBrakeCompound;
    public fixed float PadLife[4];
    public fixed float DiscLife[4];
    public int IgnitionOn;
    public int StarterEngineOn;
    public int IsEngineRunning;
    public float KerbVibration;
    public float SlipVibration;
    public float GVibration;
    public float AbsVibration;
}
