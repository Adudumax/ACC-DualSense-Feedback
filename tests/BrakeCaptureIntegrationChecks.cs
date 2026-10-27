using ACCDualSenseFeedback.Diagnostics;

internal static class BrakeCaptureIntegrationChecks
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run(Action<string, Action> check)
    {
        check("Brake raw snapshot keeps physical bytes distinct from ACC telemetry and device match", () =>
        {
            var buffer = new BrakeRawInputBuffer();
            Assert(!buffer.Read(true).Valid, "unobserved physical input was invented");
            buffer.Publish(210, 37, 123456);
            Assert(buffer.Read(true) == new BrakeRawInput(210, 37, 123456, true, true),
                "physical trigger bytes or timestamp changed");
            Assert(!buffer.Read(false).SameDevice, "input/output device mismatch was hidden");
        });
        check("Brake raw single-writer snapshots never combine bytes from different USB observations", () =>
        {
            var buffer = new BrakeRawInputBuffer();
            int finished = 0;
            Task writer = Task.Run(() =>
            {
                try
                {
                    for (int i = 1; i <= 100000; i++)
                        buffer.Publish((byte)i, unchecked((byte)~i), i);
                }
                finally { Volatile.Write(ref finished, 1); }
            });
            int observations = 0;
            do
            {
                BrakeRawInput sample = buffer.Read(true);
                if (!sample.Valid) continue;
                Assert(sample.L2 == unchecked((byte)sample.ObservedTimestamp) &&
                    sample.R2 == unchecked((byte)~sample.ObservedTimestamp), "torn USB trigger observation");
                observations++;
            } while (Volatile.Read(ref finished) == 0);
            writer.GetAwaiter().GetResult();
            Assert(observations > 0 && buffer.Read(true).ObservedTimestamp == 100000,
                "concurrent trigger observation never completed");
        });
    }
}
