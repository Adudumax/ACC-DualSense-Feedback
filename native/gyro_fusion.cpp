#define _USE_MATH_DEFINES
#include <cmath>
#include <new>
#include "vendor/GamepadMotionHelpers/GamepadMotion.hpp"

#define API extern "C" __declspec(dllexport)
struct Fusion { GamepadMotion motion; Fusion() { motion.SetCalibrationMode(GamepadMotionHelpers::Manual); motion.PauseContinuousCalibration(); } };
struct FusionResult { float qx, qy, qz, qw, gx, gy, gz, vx, vy, vz; };
API unsigned int acc_gyro_abi_version() noexcept { return 1; }
API void* acc_gyro_create() noexcept { return new(std::nothrow) Fusion(); }
API void acc_gyro_destroy(void* p) noexcept { delete static_cast<Fusion*>(p); }
API void acc_gyro_reset(void* p) noexcept { if(p) static_cast<Fusion*>(p)->motion.ResetMotion(); }
API void acc_gyro_set_bias(void* p, float x, float y, float z) noexcept {
    if (!p) return;
    auto& m = static_cast<Fusion*>(p)->motion;
    m.SetCalibrationMode(GamepadMotionHelpers::Manual);
    m.PauseContinuousCalibration();
    m.ResetContinuousCalibration();
    m.SetCalibrationOffset(x, y, z, 1);
}
API int acc_gyro_process(void* p, float x, float y, float z, float ax, float ay, float az, float dt, FusionResult* output) noexcept {
    if (!p || !output || !std::isfinite(dt) || dt <= 0 || dt > .05f) return 0;
    auto& m = static_cast<Fusion*>(p)->motion;
    // GMH already estimates shakiness. Additionally limit accelerometer correction when
    // measured magnitude indicates translation / feedback vibration. Never adapt gyro bias here.
    const float magnitude = std::sqrt(ax*ax + ay*ay + az*az);
    const bool disturbed = std::abs(magnitude - 1.f) > .15f;
    m.Settings.GravityCorrectionStillSpeed = disturbed ? .10f : 1.f;
    m.Settings.GravityCorrectionShakySpeed = disturbed ? .015f : .10f;
    m.ProcessMotion(x, y, z, ax, ay, az, dt);
    m.GetOrientation(output->qw, output->qx, output->qy, output->qz);
    m.GetGravity(output->gx, output->gy, output->gz);
    m.GetCalibratedGyro(output->vx, output->vy, output->vz);
    const float* values = reinterpret_cast<const float*>(output);
    for (unsigned i=0; i<sizeof(FusionResult)/sizeof(float); ++i) if (!std::isfinite(values[i])) { m.ResetMotion(); return 0; }
    return 1;
}
