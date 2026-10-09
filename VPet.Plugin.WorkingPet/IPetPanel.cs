namespace VPet.Plugin.WorkingPet;

/// <summary>打工面板的两种样式 (侧边面板 PetPanel / 环绕宠物 PetHud) 共用的接口</summary>
public interface IPetPanel
{
    /// <summary>刷新显示内容 (时间、进度等)</summary>
    void Refresh();
    /// <summary>首次显示前定位</summary>
    void ApplyPosition();
}
