using Microsoft.Extensions.Configuration;
using WorkContinuity.Harness.Monday;

// The Gate 3G offline harness. Its configuration comes from WCE_HARNESS_ environment variables only: no file, no
// command-line argument. See HarnessProgram for why this entry point does not run in the offline slice.
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, pressed) =>
{
    pressed.Cancel = true;
    stopping.Cancel();
};

var configuration = new ConfigurationBuilder().AddEnvironmentVariables("WCE_HARNESS_").Build();
return await HarnessProgram.RunAsync(configuration, Console.In, Console.Out, stopping.Token);
