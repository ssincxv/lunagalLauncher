using System.Collections.Generic;

namespace lunagalLauncher.Data
{
    /// <summary>
    /// 将旧版 Left/Right/Middle 配置迁移为 Rules
    /// </summary>
    public static class MouseMappingMigration
    {
        /// <summary>
        /// 将旧版 MouseActionKind 枚举值（None=0, Passthrough=1, MouseButton=2, KeyCombo=3, RepeatMacro=4）
        /// 迁移到新版（MouseButton=0, KeyCombo=1）。
        /// 旧 JSON 中的 int 值在反序列化后可能变成无效的枚举值，此方法统一修正。
        /// </summary>
        public static void MigrateActionKind(List<MouseMappingRule>? rules)
        {
            if (rules == null) return;
            foreach (var r in rules)
            {
                int raw = (int)r.Action;
                r.Action = raw switch
                {
                    0 or 1 or 4 => MouseActionKind.MouseButton,  // 旧 None/Passthrough/RepeatMacro → MouseButton
                    2 => MouseActionKind.MouseButton,              // 旧 MouseButton(2) → MouseButton(0)
                    3 => MouseActionKind.KeyCombo,                 // 旧 KeyCombo(3) → KeyCombo(1)
                    _ => MouseActionKind.MouseButton
                };
            }
        }

        /// <summary>
        /// 若已填写 KeyComboText 但 Action 仍为「模拟鼠标键」，多为下拉未同步或导入 JSON 错误；按组合键处理，否则引擎会按 SimulatedMouseButton 执行，Ctrl 等永远不会发出。
        /// </summary>
        public static void NormalizeActionWhenKeyComboTextPresent(List<MouseMappingRule>? rules)
        {
            if (rules == null) return;
            foreach (var r in rules)
            {
                if (r.Action != MouseActionKind.MouseButton) continue;
                if (string.IsNullOrWhiteSpace(r.KeyComboText)) continue;
                r.Action = MouseActionKind.KeyCombo;
            }
        }

        public static void EnsureRulesFromLegacy(RootConfig config)
        {
            var mm = config.MouseMapping;
            if (mm.Rules != null && mm.Rules.Count > 0)
            {
                MigrateActionKind(mm.Rules);
                NormalizeActionWhenKeyComboTextPresent(mm.Rules);
                mm.SchemaVersion = "2";
                return;
            }

            mm.Rules ??= new List<MouseMappingRule>();

            // 左键：按下后 200ms 内按间隔连点；按住超过阈值则透传原始左键
            if (mm.LeftClick is { Enabled: true })
            {
                mm.Rules.Add(new MouseMappingRule
                {
                    Name = "左键·短按窗口内连点",
                    Button = MousePhysicalButton.Left,
                    Trigger = MouseTriggerKind.Click,
                    HoldThresholdMs = mm.LeftClick.ShortPressThreshold,
                    RepeatIntervalMs = mm.LeftClick.AutoClickInterval,
                    Behavior = MouseBehaviorMode.RepeatWhileHeld,
                    Action = MouseActionKind.MouseButton,
                    Priority = 10
                });
                mm.Rules.Add(new MouseMappingRule
                {
                    Name = "左键·长按透传",
                    Button = MousePhysicalButton.Left,
                    Trigger = MouseTriggerKind.Hold,
                    HoldThresholdMs = mm.LeftClick.ShortPressThreshold,
                    Action = MouseActionKind.MouseButton,
                    SimulatedMouseButton = 0,
                    Behavior = MouseBehaviorMode.FireOnce,
                    Priority = 5
                });
            }

            // 右键：短按透传 / 长按模拟左键
            if (mm.RightClick is { Enabled: true })
            {
                mm.Rules.Add(new MouseMappingRule
                {
                    Name = "右键·短按透传",
                    Button = MousePhysicalButton.Right,
                    Trigger = MouseTriggerKind.Click,
                    HoldThresholdMs = mm.RightClick.LongPressThreshold,
                    Action = MouseActionKind.MouseButton,
                    SimulatedMouseButton = 1,
                    Priority = 10
                });
                mm.Rules.Add(new MouseMappingRule
                {
                    Name = "右键·长按模拟左键",
                    Button = MousePhysicalButton.Right,
                    Trigger = MouseTriggerKind.Hold,
                    HoldThresholdMs = mm.RightClick.LongPressThreshold,
                    Action = MouseActionKind.MouseButton,
                    SimulatedMouseButton = 0,
                    Priority = 5
                });
            }

            // 中键：短按透传 / 长按快捷键
            if (mm.MiddleClick is { Enabled: true })
            {
                mm.Rules.Add(new MouseMappingRule
                {
                    Name = "中键·短按透传",
                    Button = MousePhysicalButton.Middle,
                    Trigger = MouseTriggerKind.Click,
                    HoldThresholdMs = mm.MiddleClick.LongPressThreshold,
                    Action = MouseActionKind.MouseButton,
                    SimulatedMouseButton = 2,
                    Priority = 10
                });
                mm.Rules.Add(new MouseMappingRule
                {
                    Name = "中键·长按快捷键",
                    Button = MousePhysicalButton.Middle,
                    Trigger = MouseTriggerKind.Hold,
                    HoldThresholdMs = mm.MiddleClick.LongPressThreshold,
                    Action = MouseActionKind.KeyCombo,
                    KeyComboText = mm.MiddleClick.CustomHotkey,
                    Priority = 5
                });
            }

            mm.SchemaVersion = "2";
        }

        /// <summary>
        /// 将旧版「每条规则上的任务栏/边缘禁用」合并为全局开关并清零规则字段，避免与页顶全局 UI 重复。
        /// </summary>
        public static void MigrateSpatialDisablesToGlobal(MouseMappingConfig mm)
        {
            if (mm.Rules == null || mm.Rules.Count == 0)
                return;

            foreach (var r in mm.Rules)
            {
                if (r.DisableOnTaskbar)
                    mm.GlobalDisableOnTaskbar = true;
                if (r.DisableOnScreenEdges)
                    mm.GlobalDisableOnScreenEdges = true;
                r.DisableOnTaskbar = false;
                r.DisableOnScreenEdges = false;
            }
        }
    }
}
