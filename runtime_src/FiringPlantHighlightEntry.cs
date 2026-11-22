// ============================================================================
//  发射位高亮（FiringPlantHighlight）v1.0.2 —— 选发射位时「加农炮类」植物持续闪「可配置颜色的光」
//
//  需求（v1.0.0）：选中加农炮类植物（含毁灭磁力菇、玉米坚果等「含发射职能」的植物）去选发射位置时，
//        把场上这类植物置为「一直闪光」；设好发射位 / 取消 / 退出战斗后立刻恢复原样。
//  需求（v1.0.2）：闪光颜色可在配置里改，默认**深红色**（#8B0000），改完即时生效。
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
//      · 「含发射职能」= 该植物挂了 `ToggleMode = true` 的 MousePressComponent
//          CobCannon 系（Cover/Gold）与 MagnetShroomDM 都是 CannonComponent 把 OnFinishPressed
//          接到 FireAt 上（CannonComponent.cs L505-530）；玉米坚果 CornNut 自己接 OnFinishPressed
//          （TowerDefensePlantCornNut.cs L70-77, L190-205）。
//
//  ★ 为什么「没有颜色 uniform 可用」（0.33 shader + 参数清单核实过，见 _work/reports/flash-color.md）
//     unpack/Asset/Shader/TowerDefense/ShaderTowerDefenseCharacter.gdshader：
//       L8   instance uniform float brightStrength : hint_range(0.0, 0.5)   ← 只有亮度
//       L9   instance uniform float whiteStrength  : hint_range(0.0, 1.0)   ← 只有「加白」
//       L13  instance uniform int   effectFlags                             ← 染色是**位标志预置色**：
//       L2-6 const vec4 iceSpeedDownColor / charmColor / redHeatColor / puzzleColor / poisoningColor
//       L17-26 EFFECT_ASH..EFFECT_HOLOGRAM 位号（= ShaderEffectComponent.ShaderEffectFlags L20-32）
//       L43  uniform vec4 modulate : source_color            ← 唯一带颜色的 uniform，但它是
//            **材质级 uniform（不是 instance uniform）**：写它会把用同材质的所有角色一起染色；
//            而且 `SetSpriteGroupShaderParameter("modulate", …)` 走不到它 —— 宿主
//            ShaderEffectComponent.ApplyAdobeAnimateVisualParameter（ShaderEffectComponent.cs L487-502）
//            的 switch 只认 discardUpPos / discardDownPos / brightStrength / whiteStrength，
//            其它属性名只被塞进 `_visualParameters` 字典里「记着」、并不会写进材质。
//     ⇒ 结论：宿主没有「可 per-instance 写的颜色 uniform」。唯一的彩色通道是**角色的渲染色乘算**
//        （CanvasItem.Modulate），宿主自己就是这么实现「变亮」的：
//        ShaderEffectComponent.cs L504-516 ApplyBrightness →
//            node.SetRenderColorMultiplier(new Color(1+n, 1+n, 1+n))            ← 亮度落地在这里
//        L138-153 ResetRenderColorMultipliers → SetRenderColorMultiplier(Colors.White)  ← 宿主的「无色」
//        装配点：unpack/addons/AdobeAnimateEditor/Node/AdobeAnimateSprite.cs
//            L2605-2617 public void SetRenderColorMultiplier(Color)   ← **public**，公开可调
//            L2716-2719 ApplyComposedModulate: Modulate = _meshColor * _renderColorMultiplier
//            L2675-2686 SetRenderGrayscale（模仿者灰化，与颜色乘算**不同字段**）
//            L2706-2714 NotifyAncestorModulateChangedForRender（internal ⇒ Mod 调不到）
//     ⇒ 本 Mod 走 `AdobeAnimateSprite.SetRenderColorMultiplier(Color)`：
//        和宿主「悬停变亮 / 受击闪白」**同一条通道、同一个函数**，只是把原来的纯白系数
//        (1+b,1+b,1+b) 换成本 Mod 算出的**带色系数**。白色时与 v1.0.1 逐值等价（数学退化）。
//
//  ★ 为什么不改 `spriteGroup.Modulate`（也评估过，弃用）：
//      · 宿主已把 spriteGroup.Modulate 用作「全息」染色（ShaderEffectComponent.cs L579-583
//        `_baseSpriteGroupModulate * HologramRenderModulate`），我们写它＝和全息抢同一个字段；
//      · 改了祖先节点的 Modulate 后，宿主需要 `sprite.NotifyAncestorModulateChangedForRender()`
//        （AdobeAnimateSprite.cs L2706）才会让 GPU-graph / multimesh 批渲染重新提交，
//        而该方法是 **internal**，Mod 程序集调不到 ⇒ 存在「写了但画面不更新」的风险。
//     ⇒ per-sprite 的 SetRenderColorMultiplier 会自己 `_needsRenderSubmission = true` + RequestNodeRedraw
//        （L2609-2615），是宿主验证过的公开路径。
//
//  ★ 为什么不会污染其它染色（安全性）
//      · 咖啡豆 buff 的闪烁 = `blink` ⇒ effectFlags 位 256（TowerDefenseCharacterBuffCoffee.cs L48-95，
//        经 SetSpriteGroupShaderParameter("blink") → SetEffectFlag），是**另一个字段**；本 Mod 从不写
//        effectFlags，所以咖啡豆闪烁在本 Mod 的色光之上照常可见，退出时也不会被清掉。
//      · 模仿者灰化 = `SetRenderGrayscale(bool)`（AdobeAnimateSprite.cs L2675，`_renderGrayscale`
//        与 `_renderColorMultiplier` 是两个独立字段，L2718 只合成 meshColor*multiplier）⇒ 不碰。
//      · 冰霜 / 魅惑 / 红热 / 中毒 / 灰烬 / 全息 这些「游戏自己给植物上的色」= effectFlags 位
//        （C# 那边的 ShaderEffectComponent.ShaderEffectFlags L20-32）：本 Mod 在**进入前先检测**
//        `shaderEffectComponent.GetEffectFlags()`（L379-382，public），命中这些位就**跳过该株**
//        （不写、不记录），日志说明原因 ⇒ 「进入前已染色的不动」。
//      · 退出只复原自己动过的：逐株调用宿主自己的
//        `shaderEffectComponent.RefreshShaderNodes()`（ShaderEffectComponent.cs L192-250，public），
//        让宿主按它**当前的真实状态**重算亮度/灰化 —— 悬停变亮、受击闪白都能原样回来，
//        比硬写 0 更干净；组件已释放时兜底写回 `Colors.White`（= 宿主自己的「无色」，L149）。
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
	public float MaxBright;     // 亮度上限（宿主 hint_range 上限 0.5）
	public Color FlashColor;    // 闪光颜色（默认深红 #8B0000；A 当光强比例）
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
			FlashColor = new Color(0.545098f, 0f, 0f, 1f),   // #8B0000 深红 DarkRed
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
	internal const string BuildTag = "v1.0.2";

	/// <summary>配置键：闪光颜色（mod.json type=color，宿主归一化成 Godot.Color）。</summary>
	internal const string ColorKey = "color";

	/// <summary>默认闪光颜色：深红 #8B0000 = RGB(139,0,0) = (0.545,0,0,1)。</summary>
	internal const string DefaultColorHex = "#8B0000";

	/// <summary>候选植物列表的重扫间隔（帧）。0.2 秒左右，够快且不浪费。</summary>
	private const int RefreshIntervalFrames = 12;

	/// <summary>色光通道名（日志用，便于验收时一眼确认走的是哪条通道）。</summary>
	internal const string ColorChannelName = "renderColorMultiplier";

	internal static HighlightSettings Settings = HighlightSettings.Default();
	internal static XWModRuntimeContext PluginContext;

	/// <summary>一条「可瞄准植物」缓存记录。</summary>
	private sealed class AimPlant
	{
		public TowerDefenseCharacter Ch;
		public MousePressComponent Mp;
		/// <summary>该植物身上会被染色的 adobe 精灵（与宿主 ShaderEffectComponent 同一套遍历）。</summary>
		public List<AdobeAnimateSprite> Sprites;
	}

	/// <summary>「游戏自己给植物上的色」对应的 effectFlags 位；命中就跳过该株，不去压它。</summary>
	private static int _foreignTintMask = -1;

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tickCallable;
	private bool _tickConnected;
	private bool _settingsSubscribed;

	private readonly List<AimPlant> _eligible = new List<AimPlant>(16);
	private readonly List<AimPlant> _applied = new List<AimPlant>(16);
	/// <summary>复用的遍历栈（与宿主 ShaderEffectComponent 同一套深度优先，避免每帧 GC）。</summary>
	private static readonly Stack<Node2D> _walkStack = new Stack<Node2D>(32);
	private int _refreshCountdown;
	private ulong _lastTickMsec;
	private float _phase;
	private bool _frameLogged;
	private bool _errorLogged;
	private bool _aimLogged;
	private bool _skipLogged;
	private bool _colorLogged;

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

			// 无条件打一行：这是「Mod 到底有没有跑起来 + 走哪条色光通道」的第一现场证据
			GD.Print(LogPrefix + "入口已加载 " + BuildTag + "；process_frame 连接=" + connectState +
			         "；启用=" + Settings.Enabled + " 效果=" + Settings.Effect +
			         " 频率=" + Settings.Hz.ToString("0.##") + "Hz 强度上限=" + Settings.MaxBright.ToString("0.##") +
			         "；闪光颜色=" + ColorHex(Settings.FlashColor) + "（默认 " + DefaultColorHex + "）" +
			         "；色光通道=" + ColorChannelName);
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
		// ★ 先复原再拆连接：任何退出路径都不许留下「还在闪 / 还染着色」的植物
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
				GD.Print(LogPrefix + "进入发射位选择态：候选=" + _eligible.Count + " 正在瞄准=" + aimingCount +
				         " 闪光颜色=" + ColorHex(Settings.FlashColor));
			}
		}

		// ④ 推进相位 → 算出这一帧的「带色系数」
		ulong now = Time.GetTicksMsec();
		float dt = (_lastTickMsec == 0 || now < _lastTickMsec) ? 0f : (now - _lastTickMsec) / 1000f;
		if (dt > 0.25f) { dt = 0.25f; }   // 卡顿/后台回来时别让相位跳变
		_lastTickMsec = now;
		_phase += dt;
		Color mult = ComputeMultiplier(_phase);

		if (!_colorLogged)
		{
			_colorLogged = true;
			GD.Print(LogPrefix + "开始着色：颜色=" + ColorHex(Settings.FlashColor) +
			         " 通道=" + ColorChannelName + " 系数峰值=(" +
			         mult.R.ToString("0.###") + "," + mult.G.ToString("0.###") + "," + mult.B.ToString("0.###") + ")");
		}

		// ⑤ 逐株写色；不再需要的立刻复原
		for (int i = 0; i < _eligible.Count; i++)
		{
			AimPlant e = _eligible[i];
			bool want = !Settings.OnlyAiming || IsAiming(e);

			// 「进入前已被游戏自己染色」的（冰霜/魅惑/红热/中毒/灰烬/模仿者灰化/全息）⇒ 跳过，不去压它的颜色
			if (want && HasForeignTint(e.Ch))
			{
				want = false;
				if (!_skipLogged)
				{
					_skipLogged = true;
					try
					{
						GD.Print(LogPrefix + "跳过已染色植物（effectFlags=" +
						         DescribeForeignTint(e.Ch) + "），不改它的颜色");
					}
					catch { }
				}
			}

			if (want)
			{
				AimPlant rec = FindApplied(e.Ch);
				if (rec == null)
				{
					rec = e;
					_applied.Add(rec);
				}
				EnsureSprites(rec);
				ApplyGlow(rec, mult);
			}
			else
			{
				AimPlant rec = TakeApplied(e.Ch);
				if (rec != null)
				{
					RestoreGlow(rec);
				}
			}
		}

		// ⑥ 兜底：已经写过色的植物若已死亡 / 被移除 / 重扫后不再符合条件 ⇒ 马上复原
		if (_applied.Count > 0)
		{
			for (int i = _applied.Count - 1; i >= 0; i--)
			{
				AimPlant rec = _applied[i];
				TowerDefenseCharacter ch = rec.Ch;
				if (ch == null || !GodotObject.IsInstanceValid(ch) || !IsInEligible(ch))
				{
					RestoreGlow(rec);
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
		if (e.Ch == null || !GodotObject.IsInstanceValid(e.Ch) || !e.Ch.inGame)
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
	/// ① TowerDefensePlant ② inGame ③ 挂了 `ToggleMode = true` 的 MousePressComponent，
	/// 就算「含发射职能（选发射位型）」的植物。
	/// 判据完全来自宿主组件配置（.tres 的 toggleMode），以后游戏新增同类植物（如 0.33 的玉米坚果）
	/// **自动覆盖**，不用改本 Mod。
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

	private AimPlant FindApplied(TowerDefenseCharacter ch)
	{
		for (int i = 0; i < _applied.Count; i++)
		{
			if (ReferenceEquals(_applied[i].Ch, ch))
			{
				return _applied[i];
			}
		}
		return null;
	}

	private AimPlant TakeApplied(TowerDefenseCharacter ch)
	{
		for (int i = 0; i < _applied.Count; i++)
		{
			if (ReferenceEquals(_applied[i].Ch, ch))
			{
				AimPlant rec = _applied[i];
				_applied.RemoveAt(i);
				return rec;
			}
		}
		return null;
	}

	// ------------------------------------------------------------------ 色光系数
	/// <summary>
	/// 把「颜色 + 亮度曲线」算成宿主渲染色乘算系数（CanvasItem.Modulate 的分子）。
	///
	///   dir_c = 2 * (fc_c / max(fc)) - 1        // 白 ⇒ (1,1,1)；纯红 ⇒ (1,-1,-1)
	///   k     = max(fc)                          // 颜色本身多亮（越暗的光越弱）
	///   m_c   = 1 + 强度 * k * dir_c
	///
	/// · 白色 ⇒ m=(1+b,1+b,1+b) —— 与宿主自己的 brightStrength 逐值等价（v1.0.1 的行为）；
	/// · 深红 #8B0000 ⇒ (-,0.545) ⇒ m=(1+0.545b, 1-0.545b, 1-0.545b)
	///   ⇒ 红通道提亮、绿蓝压暗 = 真的是「深红色的光」；
	/// · 极暗颜色算出来的幅度太小（看不见）⇒ 自动退化成纯亮度闪（1+b），保证「选了暗色也看得见闪」。
	/// 颜色选择器的 A（不透明度）当**光强比例**用：A=1 满强度，A=0.4 = 40% 强度。
	/// </summary>
	private static Color ComputeMultiplier(float phase)
	{
		float maxB = Mathf.Clamp(Settings.MaxBright, 0.05f, 0.5f);   // 宿主 hint_range(0, 0.5)
		float b;
		if (Settings.Effect == HighlightEffect.Steady)
		{
			b = maxB;
		}
		else
		{
			float hz = Mathf.Clamp(Settings.Hz, 0.5f, 8f);
			double s = 0.5 + 0.5 * Math.Sin(phase * hz * 2.0 * Math.PI);   // 0..1
			b = maxB * (0.25f + 0.75f * (float)s);                          // 25%..100%，永远看得见
		}

		Color fc = Settings.FlashColor;
		b *= Mathf.Clamp(fc.A, 0f, 1f);      // A = 光强比例
		if (b <= 0.0005f)
		{
			return Colors.White;
		}

		float maxc = Mathf.Max(fc.R, Mathf.Max(fc.G, fc.B));
		Color mult;
		if (maxc <= 0.02f)
		{
			// 纯黑/近黑：没有色相可用 ⇒ 纯亮度闪
			mult = new Color(1f + b, 1f + b, 1f + b);
		}
		else
		{
			float dr = 2f * (fc.R / maxc) - 1f;
			float dg = 2f * (fc.G / maxc) - 1f;
			float db = 2f * (fc.B / maxc) - 1f;
			float k = Mathf.Clamp(maxc, 0f, 1f);
			mult = new Color(1f + b * k * dr, 1f + b * k * dg, 1f + b * k * db);

			// 幅度保底：太暗 / 接近中性又算不出幅度时，退化成纯亮度闪
			float amp = Mathf.Max(Mathf.Abs(mult.R - 1f), Mathf.Max(Mathf.Abs(mult.G - 1f), Mathf.Abs(mult.B - 1f)));
			if (amp < 0.15f * b)
			{
				mult = new Color(1f + b, 1f + b, 1f + b);
			}
		}
		mult.A = 1f;   // 只染色，不改透明度
		return mult;
	}

	// ------------------------------------------------------------------ 写色 / 复原
	/// <summary>
	/// 收集该植物身上会被染色的 adobe 精灵：与宿主
	/// `ShaderEffectComponent.RefreshShaderNodes()`（ShaderEffectComponent.cs L192-250）**同一套遍历**
	/// —— 从 `character.sprite` 起深度优先，凡是 AdobeAnimateSprite（含派生基类）都算。
	/// 宿主给亮度也是逐节点写（ApplyBrightness），所以配色也必须逐节点，否则只有主体变色、配件不变。
	/// </summary>
	private static void EnsureSprites(AimPlant rec)
	{
		if (rec == null || rec.Sprites != null)
		{
			return;
		}
		List<AdobeAnimateSprite> list = new List<AdobeAnimateSprite>(4);
		rec.Sprites = list;

		Node2D root = null;
		try { root = rec.Ch?.sprite; } catch { root = null; }
		if (root == null || !GodotObject.IsInstanceValid(root))
		{
			return;
		}
		_walkStack.Clear();
		_walkStack.Push(root);
		while (_walkStack.Count > 0)
		{
			Node2D node = _walkStack.Pop();
			if (!GodotObject.IsInstanceValid(node))
			{
				continue;
			}
			if (node is AdobeAnimateSprite sprite)
			{
				list.Add(sprite);
			}
			for (int i = node.GetChildCount() - 1; i >= 0; i--)
			{
				if (node.GetChild(i) is Node2D child)
				{
					_walkStack.Push(child);
				}
			}
		}
	}

	/// <summary>
	/// 写一帧的色光系数：`SetRenderColorMultiplier`（AdobeAnimateSprite.cs L2605，public）
	/// = 宿主自己「悬停变亮 / 受击闪白」用的同一个函数，只是系数带颜色。
	/// </summary>
	private void ApplyGlow(AimPlant rec, Color mult)
	{
		if (rec == null || rec.Sprites == null)
		{
			return;
		}
		for (int i = 0; i < rec.Sprites.Count; i++)
		{
			AdobeAnimateSprite sprite = rec.Sprites[i];
			if (sprite == null || !GodotObject.IsInstanceValid(sprite))
			{
				continue;
			}
			try { sprite.SetRenderColorMultiplier(mult); }
			catch (Exception e)
			{
				if (Settings.Log)
				{
					try { GD.PushWarning(LogPrefix + "写 " + ColorChannelName + " 失败：" + e.Message); } catch { }
				}
			}
		}
	}

	/// <summary>
	/// 复原一株：优先让**宿主自己**按它当前的真实状态重算（
	/// `shaderEffectComponent.RefreshShaderNodes()`，ShaderEffectComponent.cs L192-250，public）——
	/// 它会重新应用宿主自己的亮度（悬停 0.3 / 受击闪白 / 工具高亮）与模仿者灰化，
	/// 于是「我们借用的那层系数」被彻底擦掉，而玩家本来就有的高亮原样回来。
	/// 组件已释放（植物已死 / 已卸载）时兜底写回 `Colors.White`（宿主自己的「无色」，L149）。
	/// </summary>
	private static void RestoreGlow(AimPlant rec)
	{
		if (rec == null)
		{
			return;
		}
		TowerDefenseCharacter ch = rec.Ch;
		if (ch != null && GodotObject.IsInstanceValid(ch))
		{
			ShaderEffectComponent se = null;
			try { se = ch.shaderEffectComponent; } catch { se = null; }
			if (se != null && !se.IsReleased)
			{
				try
				{
					se.RefreshShaderNodes();
					return;
				}
				catch { }
			}
		}
		if (rec.Sprites == null)
		{
			return;
		}
		for (int i = 0; i < rec.Sprites.Count; i++)
		{
			AdobeAnimateSprite sprite = rec.Sprites[i];
			if (sprite == null || !GodotObject.IsInstanceValid(sprite))
			{
				continue;
			}
			try { sprite.SetRenderColorMultiplier(Colors.White); } catch { }
		}
	}

	/// <summary>把所有写过的植物立刻复原（亮度与颜色一并归零），并清空记录。</summary>
	private void RestoreAll(string reason)
	{
		for (int i = 0; i < _applied.Count; i++)
		{
			RestoreGlow(_applied[i]);
		}
		_applied.Clear();
		_aimLogged = false;
		if (Settings.Log)
		{
			try { GD.Print(LogPrefix + "恢复原样（" + reason + "）：亮度与颜色已归零"); } catch { }
		}
	}

	// ------------------------------------------------------------------ 别的染色检测
	/// <summary>
	/// 「游戏自己上的色」对应哪些 effectFlags 位 —— 直接查宿主公开字典
	/// `ShaderEffectComponent.ShaderEffectFlags`（ShaderEffectComponent.cs L20-32），不硬编码位号。
	/// 刻意**不含** cover(4) 与 blink(256)：那两个是周期性脉冲，和我们的色光是相乘叠加，不冲突。
	/// </summary>
	private static int ForeignTintMask()
	{
		if (_foreignTintMask >= 0)
		{
			return _foreignTintMask;
		}
		int mask = 0;
		try
		{
			string[] keys = { "ash", "iceSpeedDown", "hypnoses", "imitater", "redHeat", "poisoning", "hologram" };
			for (int i = 0; i < keys.Length; i++)
			{
				if (ShaderEffectComponent.ShaderEffectFlags.TryGetValue(keys[i], out int bit))
				{
					mask |= bit;
				}
			}
		}
		catch { }
		_foreignTintMask = mask;
		return mask;
	}

	private static int GetEffectFlags(TowerDefenseCharacter ch)
	{
		try
		{
			ShaderEffectComponent se = ch?.shaderEffectComponent;
			if (se != null && !se.IsReleased)
			{
				return se.GetEffectFlags();
			}
		}
		catch { }
		return 0;
	}

	private static bool HasForeignTint(TowerDefenseCharacter ch)
	{
		int mask = ForeignTintMask();
		if (mask == 0)
		{
			return false;
		}
		return (GetEffectFlags(ch) & mask) != 0;
	}

	private static string DescribeForeignTint(TowerDefenseCharacter ch)
	{
		int flags = GetEffectFlags(ch);
		int mask = ForeignTintMask();
		List<string> names = new List<string>(4);
		try
		{
			foreach (KeyValuePair<string, int> kv in ShaderEffectComponent.ShaderEffectFlags)
			{
				if ((mask & kv.Value) != 0 && (flags & kv.Value) != 0)
				{
					names.Add(kv.Key);
				}
			}
		}
		catch { }
		return flags + (names.Count > 0 ? "(" + string.Join(",", names) + ")" : "");
	}

	// ------------------------------------------------------------------ 设置
	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try { ReloadSettings(); } catch { }
		// 关掉总开关时立刻把已着色的复原；改颜色不用特殊处理 —— 下一帧就用新颜色重写（即时生效）
		try
		{
			if (!Settings.Enabled && _applied.Count > 0)
			{
				RestoreAll("配置变更：总开关关闭");
			}
			else if (Settings.Log && keys != null && keys.Count > 0)
			{
				GD.Print(LogPrefix + "配置变更：" + string.Join(",", keys) +
				         " ⇒ 颜色=" + ColorHex(Settings.FlashColor));
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
		s.FlashColor = ReadColor(ctx, ColorKey, HighlightSettings.Default().FlashColor);
		s.Log = ReadBool(ctx, "enableLog", false);
		Settings = s;
	}

	private static string ColorHex(Color c)
	{
		try { return "#" + c.ToHtml(false); } catch { return DefaultColorHex; }
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

	/// <summary>
	/// 读 color 配置。宿主 `XWModSettingsService` 会把 color 归一化成 **Godot.Color**
	/// （unpack/addons/ModEditor/ModSystem/XWModSettingsService.cs L286-297
	///  `normalized = new Color("#" + color.ToHtml())`），所以必须先认 Color；
	/// 只按 string 解析会永远读不到 ⇒ 「改了颜色没效果」。
	/// </summary>
	private static Color ReadColor(XWModRuntimeContext ctx, string key, Color def)
	{
		try
		{
			object v = ctx.Settings.Get<object>(key);
			if (v is Color c) { return c; }
			if (v is Variant vv)
			{
				switch (vv.VariantType)
				{
					case Variant.Type.Color: return vv.AsColor();
					case Variant.Type.String: v = vv.AsString(); break;
				}
			}
			string raw = v as string;
			if (raw != null && (raw.Length == 7 || raw.Length == 9) && raw[0] == '#')
			{
				try { return new Color(raw); } catch { }
			}
			if (v is System.Text.Json.JsonElement je &&
			    je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				string js = (je.GetString() ?? "").Trim();
				if ((js.Length == 7 || js.Length == 9) && js[0] == '#')
				{
					try { return new Color(js); } catch { }
				}
			}
		}
		catch { }
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
