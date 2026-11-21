// ============================================================================
//  发射位高亮（FiringPlantHighlight）v1.0.0 —— 选发射位时「加农炮类」植物持续闪光
//
//  需求：选中加农炮类植物（含毁灭磁力菇、玉米坚果等「含发射职能」的植物）去选发射位置时，
//        把场上这类植物置为「一直闪光」；设好发射位 / 取消 / 退出战斗后立刻恢复原样。
//
//  ⛔ 为什么不写 `class X : Node2D`：
//     本工程刻意不引 Godot.NET.Sdk（无源码生成器 ⇒ 没有脚本桥注册）。这种程序集里自定义的
//     Node 子类，**引擎不会回调它的 _Process/_Draw**（工作区内多个 Mod 实测踩到）。
//     ⇒ 驱动方式：`SceneTree.process_frame` 信号 + 逐帧比对状态（与 PlantPlacementMarker 同一套）。
//
//  ★ 状态判据（0.33 源码为准，全部编译期校验，无反射）：
//      · 发射位选择 = 宿主 `MousePressComponent.IsPressed`（ToggleMode 植物才可能为 true）
//          unpack/Script/Component/TowerDefense/Character/MousePressComponent/MousePressComponent.cs
//            L73-79  ToggleMode / ToggleTarget / IsMouseInside / IsPressed 公开只读属性
//            L405-409  ToggleMode 下按下 → IsPressed = true（进入选发射位）
//            L422-438  FinishAim：HideToggleTarget + IsPressed = false（选好发射位 / 点到空地）
//            L458-464  CancelAim：HideToggleTarget + IsPressed = false（右键取消，Release 动作）
//            L129-141  OnAliveChanged(false) → ResetInteraction（植物死亡/失效）
//            L108-127  OnDetaching / OnRuntimeReleased → ResetInteraction（组件卸载 / 场景结束）
//      · 「含发射职能」= 该植物挂了 `ToggleMode = true` 且 ToggleTarget 有效的 MousePressComponent
//          CobCannon 系（Cover/Gold）与 MagnetShroomDM 都是 CannonComponent 把 OnFinishPressed
//          接到 FireAt 上（CannonComponent.cs L505-530）；玉米坚果 CornNut 自己接 OnFinishPressed
//          （TowerDefensePlantCornNut.cs L70-77, L190-205）。
//
//  ★ 闪光手段（复用游戏自己的 shader 参数，不自造 shader / 不加节点）：
//      `TowerDefenseCharacter.SetSpriteGroupShaderParameter("brightStrength", v)`  ← 公开方法
//        Prefab/TowerDefense/Character/TowerDefenseCharacter.cs L3736-3743
//      正是宿主自己「鼠标悬停植物变亮」用的同一参数（MousePressComponent.cs L29-31, L699-705，
//      悬停值 hoverBrightness = 0.3），Shader 上限 hint_range(0.0, 0.5)：
//        unpack/Asset/Shader/TowerDefense/ShaderTowerDefenseCharacter.gdshader L8 `COLOR *= (1+brightStrength)`
//      ⇒ 我们只在 [0.05, 0.5] 之间逐帧写，和「悬停 / 受击闪白」共用同一个宿主通道，
//        不改材质、不改节点树、不新增 RID，退出时写回 0 即可完全复原。
//
//  ⚠️ 刻意**不**用 `blink` 通道（ShaderEffectComponent.ShaderEffectFlags["blink"] = 256）：
//      那是「咖啡豆」Buff 的闪烁位（TowerDefenseCharacterBuffCoffee.cs L48-95 反复置位/清零），
//      我们清零会误关玩家的咖啡豆闪烁 ⇒ 与其它功能打架。brightStrength 只与「悬停高亮 / 受击
//      闪白」共用，两者都是短暂视觉态，逐帧写 + 退出写 0 即可，不会污染任何游戏机制。
// ============================================================================
using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

// ============================================================================
//  配置模型
// ============================================================================
public enum HighlightEffect
{
	/// <summary>闪烁（默认）：亮度在 25%~100% 之间来回，植物始终看得见。</summary>
	Flash = 0,
	/// <summary>常亮：恒定亮度，不闪。</summary>
	Steady = 1
}

public struct HighlightSettings
{
	public bool Enabled;        // 总开关
	public bool OnlyAiming;     // 只高亮「正在选发射位」的那一株（关 = 场上所有同类都高亮）
	public HighlightEffect Effect;
	public float Hz;            // 闪烁频率（每秒明暗循环数）
	public float MaxBright;     // 亮度上限（宿主 shader 上限 0.5）
	public bool Log;            // 诊断日志

	public static HighlightSettings Default()
	{
		return new HighlightSettings
		{
			Enabled = true,
			OnlyAiming = false,
			Effect = HighlightEffect.Flash,
			Hz = 3f,
			MaxBright = 0.5f,
			Log = false
		};
	}
}

// ============================================================================
//  运行时入口
// ============================================================================
public sealed class FiringPlantHighlightEntry : IXWModRuntimeEntry
{
	internal const string LogPrefix = "[FiringPlantHighlight] ";
	internal const string BuildTag = "v1.0.0";

	/// <summary>宿主 shader 参数名（见文件头注释）。</summary>
	internal const string BrightParam = "brightStrength";

	/// <summary>候选植物列表的重扫间隔（帧）。0.2 秒左右，够快且不浪费。</summary>
	private const int RefreshIntervalFrames = 12;

	internal static HighlightSettings Settings = HighlightSettings.Default();
	internal static XWModRuntimeContext PluginContext;

	/// <summary>一条「可瞄准植物」缓存记录。</summary>
	private sealed class AimPlant
	{
		public TowerDefenseCharacter Ch;
		public MousePressComponent Mp;
	}

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tickCallable;
	private bool _tickConnected;
	private bool _settingsSubscribed;

	private readonly List<AimPlant> _eligible = new List<AimPlant>(16);
	private readonly List<TowerDefenseCharacter> _applied = new List<TowerDefenseCharacter>(16);
	private int _refreshCountdown;
	private ulong _lastTickMsec;
	private float _phase;
	private bool _frameLogged;
	private bool _errorLogged;
	private bool _aimLogged;

	// ------------------------------------------------------------------ 生命周期
	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			PluginContext = context;
			ReloadSettings();

			try
			{
				if (context?.Settings != null && !_settingsSubscribed)
				{
					context.Settings.Changed += OnSettingsChanged;
					_settingsSubscribed = true;
				}
			}
			catch { }

			string connectState = "未连接";
			try
			{
				_tree = Engine.GetMainLoop() as SceneTree;
				if (_tree == null)
				{
					connectState = "SceneTree 取不到";
				}
				else if (!_tickConnected)
				{
					_tickCallable = Callable.From(new Action(OnFrame));
					_tree.Connect("process_frame", _tickCallable);
					_tickConnected = true;
					connectState = "OK";
				}
				else
				{
					connectState = "已连接";
				}
			}
			catch (Exception e)
			{
				connectState = "异常：" + e.Message;
			}

			// 无条件打一行：这是「Mod 到底有没有跑起来」的第一现场证据
			GD.Print(LogPrefix + "入口已加载 " + BuildTag + "；process_frame 连接=" + connectState +
			         "；启用=" + Settings.Enabled + " 效果=" + Settings.Effect +
			         " 频率=" + Settings.Hz.ToString("0.##") + "Hz 亮度上限=" + Settings.MaxBright.ToString("0.##"));
		}
		catch (Exception e)
		{
			try { GD.PushWarning(LogPrefix + "Initialize 异常（已吞）：" + e.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try { ReloadSettings(); } catch { }
	}

	public void Shutdown()
	{
		// ★ 先复原再拆连接：任何退出路径都不许留下「还在闪」的植物
		try { RestoreAll("Mod 卸载"); } catch { }
		try
		{
			if (_context?.Settings != null && _settingsSubscribed)
			{
				_context.Settings.Changed -= OnSettingsChanged;
				_settingsSubscribed = false;
			}
		}
		catch { }
		try
		{
			if (_tree != null && _tickConnected)
			{
				_tree.Disconnect("process_frame", _tickCallable);
				_tickConnected = false;
			}
		}
		catch { }
		try { _eligible.Clear(); } catch { }
	}

	// ------------------------------------------------------------------ 帧驱动
	private void OnFrame()
	{
		try
		{
			if (!_frameLogged)
			{
				_frameLogged = true;
				GD.Print(LogPrefix + "帧回调已开始运行");
			}
			Tick();
		}
		catch (Exception e)
		{
			if (!_errorLogged)
			{
				_errorLogged = true;
				try { GD.PushWarning(LogPrefix + "帧回调异常（已捕获）：" + e); } catch { }
			}
		}
	}

	private void Tick()
	{
		TowerDefenseManager mgr = TowerDefenseManager.Instance;
		bool inBattle = mgr != null && GodotObject.IsInstanceValid(mgr) && mgr.IsGameRunning();

		// ① 不在战斗里 / 总开关关掉 ⇒ 立刻复原，绝不残留
		if (!inBattle || !Settings.Enabled)
		{
			if (_applied.Count > 0)
			{
				RestoreAll(!Settings.Enabled ? "总开关关闭" : "离开战斗");
			}
			if (!Settings.Enabled)
			{
				_eligible.Clear();
				_refreshCountdown = 0;
			}
			return;
		}

		// ② 候选植物（挂在场上、ToggleMode 的 MousePressComponent）定时重扫
		if (_refreshCountdown-- <= 0)
		{
			RefreshEligible(mgr);
		}
		if (_eligible.Count == 0)
		{
			if (_applied.Count > 0)
			{
				RestoreAll("场上没有候选植物");
			}
			return;
		}

		// ③ 是否正处于「发射位选择态」——只认宿主自己的状态位
		int aimingCount = 0;
		for (int i = 0; i < _eligible.Count; i++)
		{
			if (IsAiming(_eligible[i]))
			{
				aimingCount++;
			}
		}

		if (aimingCount == 0)
		{
			if (_applied.Count > 0)
			{
				RestoreAll("发射位选择已结束");
			}
			return;
		}

		if (!_aimLogged)
		{
			_aimLogged = true;
			if (Settings.Log)
			{
				GD.Print(LogPrefix + "进入发射位选择态：候选=" + _eligible.Count + " 正在瞄准=" + aimingCount);
			}
		}

		// ④ 推进相位 → 算出这一帧的亮度
		ulong now = Time.GetTicksMsec();
		float dt = (_lastTickMsec == 0 || now < _lastTickMsec) ? 0f : (now - _lastTickMsec) / 1000f;
		if (dt > 0.25f) { dt = 0.25f; }   // 卡顿/后台回来时别让相位跳变
		_lastTickMsec = now;
		_phase += dt;
		float bright = ComputeBrightness(_phase);

		// ⑤ 逐株写 shader 参数；不再需要的立刻写回 0
		for (int i = 0; i < _eligible.Count; i++)
		{
			AimPlant e = _eligible[i];
			bool want = !Settings.OnlyAiming || IsAiming(e);
			if (want)
			{
				SetBright(e.Ch, bright);
				if (!ContainsApplied(e.Ch))
				{
					_applied.Add(e.Ch);
				}
			}
			else if (RemoveApplied(e.Ch))
			{
				SetBright(e.Ch, 0f);
			}
		}

		// ⑥ 兜底：已经写过的植物若已死亡 / 被移除 / 重扫后不再符合条件 ⇒ 马上复原
		if (_applied.Count > 0)
		{
			for (int i = _applied.Count - 1; i >= 0; i--)
			{
				TowerDefenseCharacter ch = _applied[i];
				if (!GodotObject.IsInstanceValid(ch) || !IsInEligible(ch))
				{
					SetBright(ch, 0f);
					_applied.RemoveAt(i);
				}
			}
		}
	}

	// ------------------------------------------------------------------ 「是否在选发射位」
	/// <summary>
	/// 宿主权威判据：`MousePressComponent.IsPressed`（ToggleMode 植物的「已按下、等待点目标格」态）。
	/// 选好发射位（FinishAim）/ 右键取消（CancelAim）/ 植物死亡（OnAliveChanged）/ 组件卸载
	/// 都会把它清成 false ⇒ 我们不需要自己猜取消路径。
	/// </summary>
	private static bool IsAiming(AimPlant e)
	{
		if (e == null || e.Mp == null)
		{
			return false;
		}
		if (!GodotObject.IsInstanceValid(e.Ch) || !e.Ch.inGame)
		{
			return false;
		}
		MousePressComponent mp = e.Mp;
		if (mp.IsReleased || !mp.Alive)
		{
			return false;
		}
		if (mp.Lifecycle != ComponentRuntimeLifecycle.Active)
		{
			return false;
		}
		return mp.IsPressed;
	}

	// ------------------------------------------------------------------ 候选植物
	/// <summary>
	/// 重扫场上植物：`TowerDefenseManager.GetCharacter()` 里凡是
	/// ① TowerDefensePlant ② inGame ③ 挂了 `ToggleMode = true` 且 ToggleTarget 有效的
	/// MousePressComponent，就算「含发射职能（选发射位型）」的植物。
	/// 判据完全来自宿主组件配置（.tres 的 toggleMode / toggleTargetPath），
	/// 以后游戏新增同类植物（如 0.33 的玉米坚果）**自动覆盖**，不用改本 Mod。
	/// </summary>
	private void RefreshEligible(TowerDefenseManager mgr)
	{
		_refreshCountdown = RefreshIntervalFrames;
		_eligible.Clear();

		Godot.Collections.Array chars = null;
		try { chars = mgr.GetCharacter(); }
		catch { return; }
		if (chars == null)
		{
			return;
		}

		for (int i = 0; i < chars.Count; i++)
		{
			TowerDefenseCharacter ch = null;
			try { ch = chars[i].AsGodotObject() as TowerDefenseCharacter; }
			catch { ch = null; }
			if (ch == null || !GodotObject.IsInstanceValid(ch))
			{
				continue;
			}
			if (!(ch is TowerDefensePlant))
			{
				continue;
			}
			if (!ch.inGame)
			{
				continue;
			}
			ComponentManager cm = ch.componentManager;
			if (cm == null || !GodotObject.IsInstanceValid(cm))
			{
				continue;
			}
			MousePressComponent mp = null;
			try { mp = cm.GetRuntime<MousePressComponent>(); }
			catch { mp = null; }
			if (mp == null || mp.IsReleased)
			{
				continue;
			}
			if (!mp.ToggleMode)
			{
				continue;   // 非 toggle（如辣椒大炮菇的 Line 模式「点一下就开火」、只做悬停高亮的植物）
			}
			// ToggleTarget 只是宿主自己的瞄准标记节点；这里**不**拿它做硬判据（防止节点解析时序
			// 造成假阴性）。ToggleMode 来自 .tres 的 toggleMode 字段，是「点植物→再点目标格」这类
			// 植物的组件级标记，就已经等价于「有发射位置可选」。
			_eligible.Add(new AimPlant { Ch = ch, Mp = mp });
		}
	}

	private bool IsInEligible(TowerDefenseCharacter ch)
	{
		for (int i = 0; i < _eligible.Count; i++)
		{
			if (ReferenceEquals(_eligible[i].Ch, ch))
			{
				return true;
			}
		}
		return false;
	}

	private bool ContainsApplied(TowerDefenseCharacter ch)
	{
		for (int i = 0; i < _applied.Count; i++)
		{
			if (ReferenceEquals(_applied[i], ch))
			{
				return true;
			}
		}
		return false;
	}

	private bool RemoveApplied(TowerDefenseCharacter ch)
	{
		for (int i = 0; i < _applied.Count; i++)
		{
			if (ReferenceEquals(_applied[i], ch))
			{
				_applied.RemoveAt(i);
				return true;
			}
		}
		return false;
	}

	// ------------------------------------------------------------------ 亮度
	private static float ComputeBrightness(float phase)
	{
		float maxB = Mathf.Clamp(Settings.MaxBright, 0.05f, 0.5f);   // 宿主 shader hint_range(0, 0.5)
		if (Settings.Effect == HighlightEffect.Steady)
		{
			return maxB;
		}
		float hz = Mathf.Clamp(Settings.Hz, 0.5f, 8f);
		double s = 0.5 + 0.5 * Math.Sin(phase * hz * 2.0 * Math.PI);   // 0..1
		return maxB * (0.25f + 0.75f * (float)s);                     // 25%..100%，永远看得见
	}

	private static void SetBright(TowerDefenseCharacter ch, float value)
	{
		if (ch == null || !GodotObject.IsInstanceValid(ch))
		{
			return;
		}
		try { ch.SetSpriteGroupShaderParameter(BrightParam, value); }
		catch (Exception e)
		{
			if (Settings.Log)
			{
				try { GD.PushWarning(LogPrefix + "写 " + BrightParam + " 失败：" + e.Message); } catch { }
			}
		}
	}

	/// <summary>把所有写过的植物立刻写回 0（= 宿主默认「不亮」），并清空记录。</summary>
	private void RestoreAll(string reason)
	{
		for (int i = 0; i < _applied.Count; i++)
		{
			SetBright(_applied[i], 0f);
		}
		_applied.Clear();
		_aimLogged = false;
		if (Settings.Log)
		{
			try { GD.Print(LogPrefix + "恢复原样（" + reason + "）"); } catch { }
		}
	}

	// ------------------------------------------------------------------ 设置
	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try { ReloadSettings(); } catch { }
		// 关掉总开关 / 改效果时立刻按新设置重算：先把已亮的复原，下一帧再按新设置决定
		try
		{
			if (!Settings.Enabled && _applied.Count > 0)
			{
				RestoreAll("配置变更：总开关关闭");
			}
		}
		catch { }
	}

	internal static void ReloadSettings()
	{
		XWModRuntimeContext ctx = PluginContext;
		if (ctx == null)
		{
			return;
		}
		HighlightSettings s = HighlightSettings.Default();
		s.Enabled = ReadBool(ctx, "enabled", true);
		s.OnlyAiming = ReadBool(ctx, "onlyAiming", false);
		s.Effect = ReadEffect(ctx, "effect", HighlightEffect.Flash);
		s.Hz = ReadFloat(ctx, "flashHz", 3f);
		s.MaxBright = ReadFloat(ctx, "maxBright", 50f) / 100f;   // 配置里用 5..50（百分比），内部 0.05..0.5
		s.Log = ReadBool(ctx, "enableLog", false);
		Settings = s;
	}

	private static bool ReadBool(XWModRuntimeContext ctx, string key, bool def)
	{
		try
		{
			object v = ctx.Settings.Get<object>(key);
			if (v is bool b) { return b; }
			if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
		}
		catch { }
		return def;
	}

	private static string ReadString(XWModRuntimeContext ctx, string key, string def)
	{
		try
		{
			object v = ctx.Settings.Get<object>(key);
			if (v is string s && s.Length > 0) { return s.Trim(); }
			if (v is System.Text.Json.JsonElement je &&
			    je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				return (je.GetString() ?? "").Trim();
			}
		}
		catch { }
		return def;
	}

	private static HighlightEffect ReadEffect(XWModRuntimeContext ctx, string key, HighlightEffect def)
	{
		switch (ReadString(ctx, key, "").ToLowerInvariant())
		{
			case "flash": return HighlightEffect.Flash;
			case "steady": return HighlightEffect.Steady;
		}
		return def;
	}

	private static float ReadFloat(XWModRuntimeContext ctx, string key, float def)
	{
		try
		{
			object v = ctx.Settings.Get<object>(key);
			if (v is float f) { return f; }
			if (v is double d) { return (float)d; }
			if (v is int i) { return i; }
			if (v is string s && float.TryParse(s, System.Globalization.NumberStyles.Float,
			        System.Globalization.CultureInfo.InvariantCulture, out float sf)) { return sf; }
			if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
			{
				return (float)je.GetDouble();
			}
		}
		catch { }
		return def;
	}
}
