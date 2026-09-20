using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using CsPlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using CsQuestManager = FFXIVClientStructs.FFXIV.Client.Game.QuestManager;
using LClassJob = Lumina.Excel.Sheets.ClassJob;
using LParamGrow = Lumina.Excel.Sheets.ParamGrow;

namespace XivMcp.Plugin.Providers.Character;

[McpProvider("character")]
public sealed class JobProvider
{
    private static readonly Dictionary<string, string> BaseClassToJob = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GLA"] = "PLD", ["PGL"] = "MNK", ["MRD"] = "WAR", ["LNC"] = "DRG", ["ARC"] = "BRD",
        ["CNJ"] = "WHM", ["THM"] = "BLM", ["ACN"] = "SMN", ["ROG"] = "NIN",
    };

    private static readonly ConcurrentDictionary<Type, (MethodInfo Get, PropertyInfo[] Properties)> GaugeCache = new();

    private readonly IObjectTable objects;
    private readonly IPlayerState playerState;
    private readonly IJobGauges gauges;
    private readonly IDataManager data;

    public JobProvider(IObjectTable objects, IPlayerState playerState, IJobGauges gauges, IDataManager data)
    {
        this.objects = objects;
        this.playerState = playerState;
        this.gauges = gauges;
        this.data = data;
    }

    public sealed record JobLevelDto(
        uint Id,
        string Abbreviation,
        string Name,
        string Role,
        string Type,
        string? ParentClass,
        int Level,
        long Experience,
        long? ExperienceToNext,
        double? ProgressPercent,
        bool? IsMaxLevel,
        bool Unlocked,
        bool IsCurrent);

    public sealed record JobLevelsDto(JobDto? CurrentJob, int CurrentLevel, int MaxLevel, int UnlockedCount, List<JobLevelDto> Jobs);

    public sealed record JobGaugeDto(JobDto? Job, bool HasGauge, string? GaugeType, Dictionary<string, object?>? Values, string? Note);

    [McpTool("get_job_levels",
        Sources = ["client:PlayerState", "lumina:ClassJob"],
        Title = "Get class/job levels",
        Description = "Every combat class/job, crafter and gatherer with the logged-in character's level and experience. Each entry: id, abbreviation, " +
                      "name, role (tank|healer|melee|physicalRanged|magicalRanged|crafter|gatherer), type (class|job|limitedJob), parentClass " +
                      "(e.g. GLA for PLD — a job shares level/exp with its base class), level (0 = never leveled), experience, experienceToNext, " +
                      "progressPercent, isMaxLevel (null for limited jobs such as Blue Mage, whose cap differs), unlocked (jobs with a base class require their unlock quest) and isCurrent. Also returns " +
                      "currentJob, currentLevel, the account's maxLevel and unlockedCount. Use for 'what level is my X', 'which jobs can I play', " +
                      "or picking a job for content with a level requirement.")]
    public unsafe JobLevelsDto GetJobLevels()
    {
        if (objects.LocalPlayer is not { } player || !playerState.IsLoaded)
        {
            throw new McpToolException("No character is logged in.");
        }

        var ps = CsPlayerState.Instance();
        var maxLevel = ps != null && ps->MaxLevel > 0 ? ps->MaxLevel : 100;
        var currentId = player.ClassJob.RowId;
        var paramGrow = data.GetExcelSheet<LParamGrow>();
        var rows = new List<(LClassJob Row, JobLevelDto Dto)>();

        foreach (var job in data.GetExcelSheet<LClassJob>())
        {
            if (job.RowId == 0 || job.ExpArrayIndex < 0)
            {
                continue;
            }

            var abbreviation = job.Abbreviation.ExtractText();
            if (string.IsNullOrEmpty(abbreviation))
            {
                continue;
            }

            int level = playerState.GetClassJobLevel(job);
            long exp = Math.Max(0, playerState.GetClassJobExperience(job));
            long? toNext = null;
            if (level > 0 && paramGrow.TryGetRow((uint)level, out var grow) && grow.ExpToNext > 0)
            {
                toNext = grow.ExpToNext;
            }

            var parentId = job.ClassJobParent.RowId;
            var hasParent = parentId != 0 && parentId != job.RowId;
            var isJob = job.JobIndex > 0;
            var unlocked = level > 0;
            if (unlocked && isJob && hasParent && job.UnlockQuest.RowId != 0)
            {
                unlocked = CsQuestManager.IsQuestComplete(job.UnlockQuest.RowId);
            }

            bool? isMax = job.IsLimitedJob ? null : level >= maxLevel;
            rows.Add((job, new JobLevelDto(
                job.RowId,
                abbreviation,
                job.Name.ExtractText(),
                Snapshots.RoleName(job),
                job.IsLimitedJob ? "limitedJob" : isJob ? "job" : "class",
                hasParent ? job.ClassJobParent.ValueNullable?.Abbreviation.ExtractText() : null,
                level,
                exp,
                toNext,
                toNext is > 0 ? Math.Round(exp * 100.0 / toNext.Value, 1) : null,
                isMax,
                unlocked,
                job.RowId == currentId)));
        }

        var jobs = rows.OrderBy(r => r.Row.UIPriority).ThenBy(r => r.Row.RowId).Select(r => r.Dto).ToList();
        return new JobLevelsDto(
            Snapshots.Job(player.ClassJob),
            player.Level,
            maxLevel,
            jobs.Count(j => j.Unlocked),
            jobs);
    }

    [McpTool("get_job_gauge",
        Sources = ["dalamud:IJobGauges"],
        Title = "Get job gauge",
        Description = "The current job's gauge (the job-specific resource UI: e.g. PLD oath, WAR beast gauge, BLM astral fire/umbral ice and " +
                      "polyglot, SAM sen/kenki, VPR rattling coils/serpent offerings, PCT palette/canvas/motifs). Returns job, gaugeType " +
                      "(e.g. \"BLMGauge\") and values: every public property of Dalamud's typed gauge by name, with enums as strings and " +
                      "timers in the raw units the game stores (usually milliseconds). Base classes (GLA, THM, ...) report their job's gauge. hasGauge=false " +
                      "for crafters, gatherers and Blue Mage. Read-only; no rotation advice or automation.")]
    public JobGaugeDto GetJobGauge()
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        var job = Snapshots.Job(player.ClassJob);
        if (job == null)
        {
            return new JobGaugeDto(null, false, null, null, "Current class/job could not be resolved.");
        }

        var abbreviation = BaseClassToJob.GetValueOrDefault(job.Abbreviation, job.Abbreviation);
        var gaugeType = typeof(JobGaugeBase).Assembly.GetType($"Dalamud.Game.ClientState.JobGauge.Types.{abbreviation}Gauge");
        if (gaugeType == null || !typeof(JobGaugeBase).IsAssignableFrom(gaugeType))
        {
            return new JobGaugeDto(job, false, null, null, $"{job.Abbreviation} has no job gauge.");
        }

        var (get, properties) = GaugeCache.GetOrAdd(gaugeType, static type =>
        {
            var method = typeof(IJobGauges).GetMethod(nameof(IJobGauges.Get))!.MakeGenericMethod(type);
            var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(JobGaugeBase.Address)
                            && p.GetCustomAttribute<ObsoleteAttribute>() == null)
                .OrderBy(p => p.MetadataToken)
                .ToArray();
            return (method, props);
        });

        object gauge;
        try
        {
            gauge = get.Invoke(gauges, null)!;
        }
        catch (TargetInvocationException ex)
        {
            throw new McpToolException($"Could not read the {abbreviation} gauge: {ex.InnerException?.Message ?? ex.Message}");
        }

        var values = new Dictionary<string, object?>(properties.Length);
        foreach (var property in properties)
        {
            try
            {
                values[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = Convert(property.GetValue(gauge));
            }
            catch (Exception)
            {
                // A property that cannot be read in the current state is left out.
            }
        }

        var note = abbreviation != job.Abbreviation ? $"{job.Abbreviation} uses the {abbreviation} gauge; some values stay empty until the job is unlocked." : null;
        return new JobGaugeDto(job, true, gaugeType.Name, values, note);
    }

    private static object? Convert(object? value) => value switch
    {
        null => null,
        Enum e => e.ToString(),
        string s => s,
        float f => float.IsFinite(f) ? Math.Round(f, 3) : null,
        double d => double.IsFinite(d) ? Math.Round(d, 3) : null,
        IEnumerable list => list.Cast<object?>().Select(Convert).ToList(),
        _ => value,
    };
}
