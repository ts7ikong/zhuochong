using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphHelper;

namespace VPet.Plugin.WorkingPet;

/// <summary>宠物此刻的状态快照 (都是 0-100 的百分比或原始数值), 状态窗口和 AI 对话共用</summary>
public record PetSnapshot(
    string Name, int Level, double Exp, double ExpNeed, double Money, string MoodText,
    double Strength, double Food, double Drink, double Feeling, double Health, double Likability,
    string Activity, double ActivityMinutesLeft, bool Sleeping);

/// <summary>从 VPet 的存档/主控件里读出宠物状态</summary>
internal static class PetInfo
{
    public static PetSnapshot? Read(IMainWindow mw)
    {
        var save = mw.Core.Save;
        if (save == null) return null;
        var main = mw.Main;

        string activity = "空闲";
        double left = 0;
        bool sleeping = main.State == VPet_Simulator.Core.Main.WorkingState.Sleep;
        if (sleeping)
        {
            activity = "睡觉";
        }
        else if (main.State == VPet_Simulator.Core.Main.WorkingState.Work && main.NowWork is { } w)
        {
            activity = w.Type switch
            {
                Work.WorkType.Work => $"工作（{w.NameTrans}）",
                Work.WorkType.Study => $"学习（{w.NameTrans}）",
                _ => $"玩耍（{w.NameTrans}）",
            };
            left = Math.Max(0, w.Time - (DateTime.Now - main.WorkTimer.StartTime).TotalMinutes);
        }
        else if (main.State == VPet_Simulator.Core.Main.WorkingState.Travel)
        {
            activity = "旅行中";
        }

        return new PetSnapshot(
            save.Name, save.Level, save.Exp, save.LevelUpNeed(), save.Money, MoodText(save.Mode),
            Pct(save.Strength, save.StrengthMax), Pct(save.StrengthFood, save.StrengthMax), Pct(save.StrengthDrink, save.StrengthMax),
            Pct(save.Feeling, save.FeelingMax), Math.Max(0, Math.Min(save.Health, 100)), Pct(save.Likability, save.LikabilityMax),
            activity, left, sleeping);
    }

    public static string MoodText(IGameSave.ModeType mode) => mode switch
    {
        IGameSave.ModeType.Happy => "开心",
        IGameSave.ModeType.Nomal => "普通",
        IGameSave.ModeType.PoorCondition => "情绪低落",
        IGameSave.ModeType.Ill => "生病了",
        _ => "普通",
    };

    /// <summary>一句话描述宠物现状, 放进 AI 提示词里, 让它用宠物的口吻回话</summary>
    public static string Describe(IMainWindow mw, string ownerState)
    {
        var p = Read(mw);
        if (p == null) return "";
        string need = "";
        if (p.Food < 30) need += "，很饿";
        if (p.Drink < 30) need += "，很渴";
        if (p.Strength < 25) need += "，很累";
        return $"你叫{p.Name}，Lv.{p.Level}，心情{p.MoodText}（心情值{p.Feeling:0}%，饱腹{p.Food:0}%，口渴{p.Drink:0}%，体力{p.Strength:0}%）{need}；"
             + $"你正在{p.Activity}；主人现在{ownerState}";
    }

    private static double Pct(double value, double max) => max <= 0 ? 0 : Math.Max(0, Math.Min(value / max * 100, 100));
}
