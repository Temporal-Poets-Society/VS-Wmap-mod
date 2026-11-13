using System;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Wmap.Core;
using Wmap.Integrations;

namespace Wmap.Client;

/// <summary>Worldwide weather and monthly prevailing winds, sampled in the background. For now, a Jonas barometer
/// grants the same layers in gameplay as creative mode; the item/granted sight is still required.</summary>
public sealed class WindLayer : IDisposable
{
    readonly ICoreClientAPI api;
    readonly WmapClient client;
    public readonly SeaWinds Sea;
    /// <summary>Earth's air pressure for the barometer (WM-0006), or null without Earth.</summary>
    public readonly EarthPressure Pressure;
    CancellationTokenSource running;
    Task<float[]> sampling;
    int sampledChoice = -1, pendingChoice = -1;
    double sampledHours = double.NaN, pendingHours;
    object sampledModel, pendingModel;

    public WindLayer(ICoreClientAPI api, WmapClient client)
    {
        this.api = api; this.client = client;
        Sea = SeaWinds.Find(api);
        Pressure = EarthPressure.Find(api);
    }

    /// <summary>The barometer's reading now at a place: "1004 hPa, falling" (Earth's pressure and its three-hour tendency),
    /// null without Earth's weather.</summary>
    public string Barometer(double x, double z) => Pressure?.Reading(x, z, api.World.Calendar.TotalHours);

    public bool Available => Sea != null;
    public bool Ready => sampledModel != null && sampledModel == Sea.Model && sampledChoice == client.Config.WindLayer;

    /// <summary>Game hours the current choice is evaluated at.</summary>
    public double Hours(int choice) => WindGrid.Hours(choice, api.World.Calendar.TotalHours, Sea.DaysPerYear, Sea.HoursPerDay);

    /// <summary>Main thread with the GL context: update the chosen worldwide layer, only with permission.</summary>
    public bool Prepare(bool development, string sight)
    {
        if (Sea == null || !WindGrid.Visible(client.Config.WindLayer, development, sight != null)) return false;
        Update();
        return Ready;
    }

    void Update()
    {
        int choice = client.Config.WindLayer;
        object model = Sea.Model;
        if (sampling != null)
        {
            if (!sampling.IsCompleted) return;
            if (sampling.Status == TaskStatus.RanToCompletion)
            {
                client.Renderer.SetWind(sampling.Result, WindGrid.Width, WindGrid.Height, client.Topology.Wraps);
                sampledChoice = pendingChoice; sampledHours = pendingHours; sampledModel = pendingModel;
            }
            else if (sampling.Exception != null) api.Logger.Warning("[wmap] Wind layer sampling failed: {0}", sampling.Exception.InnerException?.Message);
            sampling = null;
            running?.Dispose(); running = null;
        }
        if (model == null || !Sea.Ready) return;
        double hours = Hours(choice);
        double stale = WindGrid.IsWeather(choice) ? 0.25 : choice == WindGrid.PrevailingNow ? Sea.HoursPerDay : 1e-6;
        if (choice == sampledChoice && model == sampledModel && Math.Abs(hours - sampledHours) < stale) return;
        var wind = Sea.Sampler(WindGrid.IsWeather(choice));
        if (wind == null) return;
        var topology = client.Topology;
        running = new CancellationTokenSource();
        var token = running.Token;
        pendingChoice = choice; pendingHours = hours; pendingModel = model;
        sampling = Task.Run(() => WindGrid.Build((x, z) => wind(x, z, hours), topology, token), token);
    }

    /// <summary>Wind at a place for the selected worldwide layer, for the cursor readout.</summary>
    public string Describe(double x, double z)
    {
        if (Reading(x, z) is not { } reading) return null;
        string belt = Sea.BeltName(z, Hours(client.Config.WindLayer));
        string text = $"Wind ({WindGrid.Name(client.Config.WindLayer)}): {reading}";
        if (belt != null) text += ", " + belt;
        // Pressure belongs to the weather now; the prevailing layers have none.
        if (WindGrid.IsWeather(client.Config.WindLayer) && Pressure?.At(x, z, api.World.Calendar.TotalHours) is double hpa) text += $", {hpa:0} hPa";
        if (Sea.Mode == "Off") text += " (Sea's wind model is off in this world)";
        return text;
    }

    /// <summary>The selected layer at a place: "from the SW, 21 kn (strong breeze)".</summary>
    public string Reading(double x, double z)
    {
        int choice = client.Config.WindLayer;
        if (Sea == null || !Sea.Ready || choice == WindGrid.Off) return null;
        double hours = Hours(choice);
        var wind = WindGrid.IsWeather(choice) ? Sea.Weather(x, z, hours) : Sea.Prevailing(x, z, hours);
        double strength = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
        if (strength < 0.02) return "calm";
        string force = Sea.Beaufort(strength);
        return $"from the {WindGrid.From(wind.X, wind.Z)}, {WindGrid.Knots(strength):0} kn" + (force != null ? $" ({force})" : "");
    }

    public void Dispose() { running?.Cancel(); running?.Dispose(); }
}
