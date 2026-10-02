namespace CodexBackupManager.App.ViewModels;

/// <summary>(Phase 9_U-09) 트리 줄 하나의 체크를 키보드(Space)로 바꿀 수 있는 모델.</summary>
public interface ICheckToggle
{
    /// <summary>마우스로 그 줄의 체크박스를 누른 것과 같이 체크를 바꾼다.</summary>
    void ToggleCheck();
}
