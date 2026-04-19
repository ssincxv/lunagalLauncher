using System;
using System.Collections.Generic;

namespace lunagalLauncher.Data
{
    /// <summary>
    /// 物理鼠标键（映射源）
    /// </summary>
    public enum MousePhysicalButton
    {
        Left = 0,
        Right = 1,
        Middle = 2,
        /// <summary>侧键 1（X1，与 WM_XBUTTON 一致）</summary>
        XButton1 = 3,
        /// <summary>侧键 2（X2）</summary>
        XButton2 = 4
    }

    /// <summary>
    /// 触发方式
    /// </summary>
    public enum MouseTriggerKind
    {
        /// <summary>单击释放（短按）</summary>
        Click = 0,
        /// <summary>按住超过阈值</summary>
        Hold = 1,
        /// <summary>双击</summary>
        DoubleClick = 2,
        /// <summary>连击（时间窗内累计次数）</summary>
        MultiClick = 3
    }

    /// <summary>
    /// 触发后行为模式（按住时是否持续）
    /// </summary>
    public enum MouseBehaviorMode
    {
        /// <summary>触发一次</summary>
        FireOnce = 0,
        /// <summary>按住期间按 RepeatInterval 持续触发</summary>
        RepeatWhileHeld = 1
    }

    /// <summary>
    /// 执行动作类型
    /// </summary>
    public enum MouseActionKind
    {
        /// <summary>模拟鼠标按键（可指定左/右/中）</summary>
        MouseButton = 0,
        /// <summary>单键或组合键（如 Ctrl、Shift+Z）</summary>
        KeyCombo = 1
    }

    /// <summary>
    /// 进程白名单模式
    /// </summary>
    public enum MouseContextWhitelistMode
    {
        /// <summary>仅列表内进程生效</summary>
        IncludeOnly = 0,
        /// <summary>列表内进程不生效</summary>
        Exclude = 1
    }

    /// <summary>
    /// 单条鼠标映射规则（同一物理键可绑定多条，按优先级匹配）
    /// </summary>
    public class MouseMappingRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>显示名称</summary>
        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        /// <summary>越大越优先</summary>
        public int Priority { get; set; } = 0;

        public MousePhysicalButton Button { get; set; } = MousePhysicalButton.Left;

        /// <summary>
        /// 0 表示物理键为鼠标 <see cref="Button"/>；非 0 时为 Win32 虚拟键码（键盘作映射源）。
        /// </summary>
        public int PhysicalKeyVirtualKey { get; set; } = 0;

        /// <summary>非 0 时由 Raw Input 的 RI_MOUSE_* 按下沿匹配（与 Pointer/LL 标准键无关）。</summary>
        public ushort PhysicalMouseRawButtonFlag { get; set; } = 0;

        /// <summary>非 0 时由 Raw Input 的 ulRawButtons 位掩码匹配（部分品牌扩展键仅走此路径）。</summary>
        public uint PhysicalMouseRawButtonsMask { get; set; } = 0;

        /// <summary>非 0 时由 WM_INPUT RIM_TYPEHID 报告内容哈希匹配（部分鼠标扩展键走独立 HID 集合，无标准 RI/ulRawButtons）。</summary>
        public ulong PhysicalMouseHidSignature { get; set; } = 0;

        public MouseTriggerKind Trigger { get; set; } = MouseTriggerKind.Click;

        /// <summary>Hold / 双击判定等时间阈值（毫秒）</summary>
        public int HoldThresholdMs { get; set; } = 200;

        /// <summary>连发间隔（毫秒）</summary>
        public int RepeatIntervalMs { get; set; } = 100;

        public MouseBehaviorMode Behavior { get; set; } = MouseBehaviorMode.FireOnce;

        public MouseActionKind Action { get; set; } = MouseActionKind.MouseButton;

        /// <summary>模拟鼠标键：0=左 1=右 2=中</summary>
        public int SimulatedMouseButton { get; set; } = 0;

        /// <summary>组合键文本，如 "Shift+Z" 或 "Ctrl"</summary>
        public string KeyComboText { get; set; } = string.Empty;

        /// <summary>进程白名单（可填 exe 名如 notepad.exe 或完整路径）</summary>
        public List<string> ProcessFilter { get; set; } = new List<string>();

        public MouseContextWhitelistMode ContextMode { get; set; } = MouseContextWhitelistMode.IncludeOnly;

        /// <summary>空列表表示不限制（仅当 ProcessFilter 非空时生效）</summary>
        public bool RestrictToProcessList { get; set; } = false;

        /// <summary>已迁移至 <see cref="MouseMappingConfig.GlobalDisableOnTaskbar"/>；保留仅用于旧配置反序列化后一次性合并。</summary>
        public bool DisableOnTaskbar { get; set; }

        /// <summary>已迁移至 <see cref="MouseMappingConfig.GlobalDisableOnScreenEdges"/>；保留同上。</summary>
        public bool DisableOnScreenEdges { get; set; }
    }
}
