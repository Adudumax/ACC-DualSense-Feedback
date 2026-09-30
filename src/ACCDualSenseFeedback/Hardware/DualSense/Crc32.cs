namespace ACCDualSenseFeedback.Hardware.DualSense;

internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    // Sony Bluetooth output CRC is standard reflected CRC-32 over the header
    // byte 0xA2 followed by the first 74 report bytes.
    public static uint ComputeBluetoothOutput(ReadOnlySpan<byte> reportWithoutCrc)
    {
        uint crc = 0xFFFFFFFFu;
        crc = Update(crc, 0xA2);
        foreach (byte value in reportWithoutCrc)
            crc = Update(crc, value);
        return ~crc;
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? Polynomial : 0u);
        return crc;
    }
}
