using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EvmMatching.Data;
using EvmMatching.Runner;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
try
{
    if (args.Length == 0) throw new ArgumentException("Commands: fetch <rpc> <from> <to> | normalize | prepare <partition> <name> <wSize> <hitRate> <updateRate> <seconds> <seed> | run <config> | run-one <config> | run-series <plan> | handoff");
    switch (args[0])
    {
        case "fetch": await Preparation.Fetch(args[1], long.Parse(args[2]), long.Parse(args[3])); break;
        case "normalize": Preparation.Normalize(); break;
        case "calibrate": Calibration.Execute(args[1], args.Length > 2 ? int.Parse(args[2]) : 1, args.Length > 3 ? args[3] : "data/processed/tuning.bin"); break;
        case "simulate":
            var simulation = Files.ReadJson<SimulationPlan>(args[1]);
            Files.SaveJson($"configs/{simulation.Name}.json", Simulation.Prepare(simulation)); break;
        case "prepare":
            var config = Preparation.PrepareScenario(args[1], args[2], int.Parse(args[3]), double.Parse(args[4]), double.Parse(args[5]), double.Parse(args[6]), int.Parse(args[7]));
            Files.SaveJson($"configs/{args[2]}.json", config); break;
        case "run": return Series.Launch(Files.ReadJson<ExperimentConfig>(args[1]), args[1]);
        case "run-one": return RunOne.Execute(args[1]);
        case "run-series": return Series.Execute(args[1]);
        case "handoff": Series.Handoff(); break;
        default: throw new ArgumentException("Unknown command: " + args[0]);
    }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
