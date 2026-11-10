namespace ItemDetails;

/// <summary>沿用原版陆生动物阶段的只读提示。日龄差与每次结算推进多少无关。</summary>
internal static class AnimalStageProgress
{
    // 对应 GetAnimalGrowthStageDisplay 中 growthStages[1..4]；[5] 用于老死判定。
    private static readonly (string Key, string Fallback)[] Stages =
    {
        ("husbandry_stage_juvenile", "Juvenile"),
        ("husbandry_stage_adolescent", "Adolescent"),
        ("husbandry_stage_adult", "Adult"),
        ("husbandry_stage_elder", "Elder"),
    };

    internal static bool Append(TooltipSections.Writer output, GameItem item)
    {
        if (!ModSettings.InfoEnhance
            || !GameRead.TagEnabled(item, HusbandryHelper.ANIMAL_ITEM_TAG)
            || GameRead.TagEnabled(item, HusbandryHelper.ANIMAL_IS_DEAD_TAG))
            return false;

        string? speciesId = GameRead.TagString(item, HusbandryHelper.ANIMAL_SPECIES_TAG);
        if (string.IsNullOrEmpty(speciesId)
            || !AnimalSpecies.All.TryGetValue(speciesId, out var species)
            || species?.growthStages is not { } thresholds
            || thresholds.Length < Stages.Length + 2)
            return false;

        // 原版新生动物尚无日龄标签，第一次过夜前按 0 处理，读 tooltip 时不初始化标签。
        int age = GameRead.TagInt(item, HusbandryHelper.ANIMAL_AGE_TAG) ?? 0;
        if (age < 0 || age >= thresholds[Stages.Length]) return false;

        int next = 1;
        for (int stage = 1; stage <= Stages.Length; stage++)
        {
            if (thresholds[stage] < thresholds[stage - 1]) return false;
            if (thresholds[stage] <= age)
                next = stage + 1;
        }

        // 共用一个门槛时，原版从最高阶段往下判定，会跳过同日龄的中间阶段。
        while (next < Stages.Length && thresholds[next] == thresholds[next + 1]) next++;

        string name = StageName(item, next);
        int remaining = thresholds[next] - age;
        output.Field("nextStage", name);
        output.Field("remainingAgeDays", remaining);
        output.AddLine(Loc.T("animal.next_stage", "Next stage: {0} (+{1} {1|growth day|growth days})", name, remaining),
            RenderHandler.ColorPalette.LightCyan);
        return true;
    }

    private static string StageName(GameItem item, int stage)
    {
        var (key, fallback) = Stages[stage - 1];
        try
        {
            string name = LocHelper.GetLocalizedMechanicOrDefault(key, fallback);
            var gender = GameRead.TagString(item, HusbandryHelper.ANIMAL_SEXE_TAG) switch
            {
                "MALE" => LocGenderHelper.LocGender.Male,
                "FEMALE" => LocGenderHelper.LocGender.Female,
                _ => LocGenderHelper.LocGender.Neutral,
            };
            return LocGenderHelper.Resolve(name, gender);
        }
        catch { return fallback; }
    }
}
