using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「发射位高亮」Mod —— 托管运行时入口。
///
/// ── 需求 ────────────────────────────────────────────────────────────────
/// 选择加农炮类植物（包括毁灭磁力菇等「含发射职能」的植物）选择发射位置时，
/// 把场上这类植物置为「一直闪光」；取消发射或设置好发射位置后取消闪光。
///
/// ── 0.33 源码依据（全部实查，行号见 _work/reports/highlight.md）──────────
/// · **选位态字段** = `MousePressComponent.IsPressed`
///   （`unpack/Script/Component/TowerDefense/Character/MousePressComponent/MousePressComponent.cs:79`）。
///   进入：`HandlePress` :400-402 `if (ToggleMode) { IsPressed = true; UpdateAimTarget(pointerPosition); }`；
///   完成：`FinishAim` :422-426 `HideToggleTarget(); IsPressed = false;` → `OnFinishPressed`（开火）；
///   取消：`CancelAim` :458-461 `IsPressed = false` → `OnReleased`；
///   死亡/清理：`OnAliveChanged` :129-136 → `ResetInteraction` :669。
///   ⇒只要 `IsPressed` 变 false，本 Mod 下一帧就恢复。
/// · **发射职能** =植物挂 `CannonComponent`
///   （`unpack/Script/Component/TowerDefense/Character/CannonComponent/CannonComponent.cs:6`；
///   开火入口 :734 `FireAt`；信号绑定 :511-527 `ConnectMouseSignals`）。
/// · **一直闪光** = 角色 shader 的 `EFFECT_BLINK` 效果位（=256）
///   （`unpack/Asset/Shader/TowerDefense/ShaderTowerDefenseCharacter.gdshader:25` `const int EFFECT_BLINK = 1 << 8;`，
///   :230-233 `blinkColor = 0.75 + sin(TIME*15.0)*0.25; COLOR.rgb *= blinkColor;`）。
///   写入口：`unpack/Prefab/TowerDefense/Character/TowerDefenseCharacter.cs:3736-3742`
///   `SetSpriteGroupShaderParameter("blink", true/false)` → `ShaderEffectComponent`
///   （`unpack/Script/Component/TowerDefense/Character/ShaderEffectComponent/ShaderEffectComponent.cs:30`
///   `{ "blink", 256 }`、:394 `SetEffectFlag`、:379 `GetEffectFlags`）。
///   原生同款用法：`unpack/Resource/TowerDefense/Character/Buff/TowerDefenseCharacterBuffCoffee.cs:48,61,79,84,94`。
/// · **全场枚举**：`unpack/Core/TowerDefenseManager/TowerDefenseManager.cs:142`
///   `public TowerDefenseBattleCharacterRegistry characterRegistry;`
///   → `registry.GetCharactersIntersectingRectList(rect)`（同款写法已验证于 `mods/Glove/runtime_src/GloveEntry.cs:1720-1760`）。
///
/// ── 铁律 ────────────────────────────────────────────────────────────────
/// · 入口三回调一律 try/catch（抛出 = 整包回滚，玩家看到"加载失败"）。
/// · 本程序集**没有 Godot 源码生成器** ⇒ 自定义 Node 子类的 `_Process` 不会被调用，
///   每帧逻辑一律挂 `SceneTree.process_frame` 信号。
/// · 不抢别的闪烁：进入前 `blink` 位本来就是 1 的（咖啡豆 buff / 禁忌豆）**不记录、不恢复**，
///   退出时只把我们自己打开的位关掉。
/// </summary>
public sealed partial class FiringPlantHighlightEntry : IXWModRuntimeEntry
{
	private const string LogPrefix = "[FiringPlantHighlight] ";

	// ── 配置键（与 mod.json 的 settings[].key 一一对应）──
	private const string KEnabled = "enabled";
	private const string KEnableLog = "enableLog";

	/// <summary>`ShaderEffectFlags["blink"]`（ShaderEffectComponent.cs:30）。</summary>
	private const int BlinkFlag = 256;

	/// <summary>全场查询用的超大矩形（Glove 同款）。</summary>
	private static readonly Rect2 WorldQueryRect = new Rect2(-10000f, -10000f, 20000f, 20000f);

	// ================================================================ 运行状态

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tickCallable;
	private bool _started;

	private bool _enabled = true;
	private bool _log;

	/// <summary>被本 Mod 打开 blink 的植物（InstanceId → 植物）。只存"进入前 blink 位是 0"的。</summary>
	private readonly Dictionary<ulong, TowerDefenseCharacter> _blinking =
		new Dictionary<ulong, TowerDefenseCharacter>();

	/// <summary>注册表内部复用缓冲的拷贝目标（避免遍历时被宿主回调改动）。</summary>
	private readonly List<TowerDefenseCharacter> _scratch = new List<TowerDefenseCharacter>();

	/// <summary>本 Mod 当前是否认为"正在选发射位"（用于只在翻转时打日志）。</summary>
	private bool _aiming;

	/// <summary>异常告警只报一次，避免刷日志。</summary>
	private bool _faultReported;

	// ================================================================ 入口三回调

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			ReloadSettings();
			Info("初始化完成；PackageRoot=" + ((context == null) ? "<null>" : context.PackageRoot)
				+ "；enabled=" + _enabled);
		}
		catch (Exception ex)
		{
			SafeWarn("Initialize 异常（已吞）：" + ex.Message);
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				SafeWarn("拿不到 SceneTree，本 Mod 不会生效（游戏其余部分不受影响）。");
				return;
			}
			_tickCallable = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tickCallable);
			_started = true;
			try
			{
				if (_context?.Settings != null) { _context.Settings.Changed += OnSettingsChanged; }
			}
			catch { }
			// ★ 不受 enableLog 门控：每次启动只打 1 行，用来确认"包真的加载且入口跑起来了"。
			GD.Print(LogPrefix + "v1.0.0 ready（发射位高亮；enableLog=" + (_log ? "on" : "off") + "）");
		}
		catch (Exception ex)
		{
			SafeWarn("OnAllModsLoaded 异常（已吞）：" + ex.Message);
		}
	}

	public void Shutdown()
	{
		try
		{
			try
			{
				if (_context?.Settings != null) { _context.Settings.Changed -= OnSettingsChanged; }
			}
			catch { }
			// 卸载/重载前把闪光全部还原，绝不留残留。
			try { RestoreAll(); } catch { }
			try
			{
				if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
				{
					_tree.Disconnect("process_frame", _tickCallable);
				}
			}
			catch { }
		}
		catch (Exception ex)
		{
			SafeWarn("Shutdown 异常（已吞）：" + ex.Message);
		}
		finally
		{
			_started = false;
		}
	}

	// ================================================================ 每帧对账

	private void OnFrame()
	{
		try
		{
			if (!_enabled)
			{
				// 配置关掉 = 立刻恢复（不等下一局）。
				RestoreAll();
				_aiming = false;
				return;
			}

			TowerDefenseManager manager = TowerDefenseManager.Instance;
			if (!GodotObject.IsInstanceValid(manager) || !manager.IsGameRunning())
			{
				// 关卡结束 / 回主菜单 / RTB：清空记录，不做无谓的高亮。
				RestoreAll();
				_aiming = false;
				return;
			}

			TowerDefenseBattleCharacterRegistry registry = null;
			try { registry = manager.characterRegistry; } catch { }
			if (registry == null || !GodotObject.IsInstanceValid(registry))
			{
				RestoreAll();
				_aiming = false;
				return;
			}

			CollectPlants(registry);

			// ① 是否有「有发射职能的植物」正在选发射位（IsPressed == true）。
			bool aiming = false;
			for (int i = 0; i < _scratch.Count; i++)
			{
				TowerDefenseCharacter plant = _scratch[i];
				if (!HasCannonComponent(plant)) { continue; }
				if (IsAiming(plant)) { aiming = true; break; }
			}

			if (aiming)
			{
				if (!_aiming) { Info("进入发射位选择态 → 开始高亮有发射职能的植物。"); }
				_aiming = true;
				for (int i = 0; i < _scratch.Count; i++)
				{
					TowerDefenseCharacter plant = _scratch[i];
					if (!HasCannonComponent(plant)) { continue; }
					ApplyBlink(plant);
				}
				// 已经死掉/被移除的植物从记录里摘掉（节点没了，闪光自然也没了）。
				PruneDead();
			}
			else
			{
				if (_aiming) { Info("退出发射位选择态 → 恢复植物外观。"); }
				_aiming = false;
				RestoreAll();
			}
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				SafeWarn("OnFrame 异常（已吞，本次会话不再重复报）：" + ex);
			}
		}
	}

	// ================================================================ 判定与写入

	/// <summary>把注册表的全场列表复制到自有缓冲（宿主返回的是内部复用缓冲）。</summary>
	private void CollectPlants(TowerDefenseBattleCharacterRegistry registry)
	{
		_scratch.Clear();
		List<TowerDefenseCharacter> list = null;
		try { list = registry.GetCharactersIntersectingRectList(WorldQueryRect); } catch { return; }
		if (list == null) { return; }
		int count = 0;
		try { count = list.Count; } catch { return; }
		for (int i = 0; i < count; i++)
		{
			TowerDefenseCharacter character = null;
			try { character = list[i]; } catch { continue; }
			if (character == null || !GodotObject.IsInstanceValid(character)) { continue; }
			if (!(character is TowerDefensePlant)) { continue; }
			try
			{
				if (character.die || character.isDestroy) { continue; }
			}
			catch { }
			_scratch.Add(character);
		}
	}

	/// <summary>「含发射职能」= 挂了 CannonComponent（加农炮 / 毁灭磁力菇 / 玉米炮 …）。</summary>
	private static bool HasCannonComponent(TowerDefenseCharacter character)
	{
		if (character == null || !GodotObject.IsInstanceValid(character)) { return false; }
		try
		{
			ComponentManager manager = character.componentManager;
			if (manager == null) { return false; }
			CannonComponent cannon = manager.GetRuntime<CannonComponent>();
			return cannon != null && !cannon.IsReleased;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>「正在选发射位」= MousePressComponent 处于 Toggle 瞄准态（IsPressed）。</summary>
	private static bool IsAiming(TowerDefenseCharacter character)
	{
		if (character == null || !GodotObject.IsInstanceValid(character)) { return false; }
		try
		{
			ComponentManager manager = character.componentManager;
			if (manager == null) { return false; }
			MousePressComponent press = manager.GetRuntime<MousePressComponent>();
			if (press == null || press.IsReleased) { return false; }
			return press.IsPressed;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>打开该植物的 blink 位（若进入前本来就是 1，则不接管、不记录）。</summary>
	private void ApplyBlink(TowerDefenseCharacter plant)
	{
		try
		{
			if (plant == null || !GodotObject.IsInstanceValid(plant)) { return; }
			ulong id = 0UL;
			try { id = plant.GetInstanceId(); } catch { }
			if (id == 0UL) { return; }
			if (_blinking.ContainsKey(id)) { return; }
			if (ReadBlink(plant))
			{
				// 别的效果（咖啡豆 / 禁忌豆）已经在闪：不接管，退出时也就不碰。
				return;
			}
			plant.SetSpriteGroupShaderParameter("blink", true);
			_blinking[id] = plant;
		}
		catch { }
	}

	/// <summary>读该植物当前的 blink 效果位。</summary>
	private static bool ReadBlink(TowerDefenseCharacter plant)
	{
		try
		{
			ShaderEffectComponent fx = plant.shaderEffectComponent;
			if (fx == null || fx.IsReleased) { return false; }
			return (fx.GetEffectFlags() & BlinkFlag) != 0;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>把本 Mod 打开过的 blink 全部关掉（幂等；节点已失效的直接丢弃）。</summary>
	private void RestoreAll()
	{
		if (_blinking.Count == 0) { return; }
		foreach (KeyValuePair<ulong, TowerDefenseCharacter> kv in _blinking)
		{
			TowerDefenseCharacter plant = kv.Value;
			try
			{
				if (plant != null && GodotObject.IsInstanceValid(plant))
				{
					plant.SetSpriteGroupShaderParameter("blink", false);
				}
			}
			catch { }
		}
		_blinking.Clear();
	}

	/// <summary>把已死亡/已释放的植物从记录里摘掉（它们节点没了，不需要再恢复）。</summary>
	private void PruneDead()
	{
		if (_blinking.Count == 0) { return; }
		List<ulong> dead = null;
		foreach (KeyValuePair<ulong, TowerDefenseCharacter> kv in _blinking)
		{
			TowerDefenseCharacter plant = kv.Value;
			bool gone = false;
			try
			{
				if (plant == null || !GodotObject.IsInstanceValid(plant)) { gone = true; }
				else if (plant.die || plant.isDestroy) { gone = true; }
			}
			catch { gone = true; }
			if (gone)
			{
				(dead ?? (dead = new List<ulong>())).Add(kv.Key);
			}
		}
		if (dead == null) { return; }
		for (int i = 0; i < dead.Count; i++) { _blinking.Remove(dead[i]); }
	}

	// ================================================================ 配置

	private void ReloadSettings()
	{
		try
		{
			_enabled = ReadBool(KEnabled, true);
			_log = ReadBool(KEnableLog, false);
		}
		catch { }
	}

	private void OnSettingsChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		try
		{
			ReloadSettings();
			Info("配置已刷新：enabled=" + _enabled);
		}
		catch { }
	}

	/// <summary>读 bool（兼容 bool / "true" / JsonElement；取不到用默认值）。</summary>
	private bool ReadBool(string key, bool def)
	{
		try
		{
			if (_context?.Settings == null) { return def; }
			object raw = _context.Settings.Get<object>(key);
			if (raw == null) { return def; }
			if (raw is bool b) { return b; }
			if (raw is string s && bool.TryParse(s, out bool sb)) { return sb; }
			if (raw is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
			return def;
		}
		catch { return def; }
	}

	private void Info(string message)
	{
		if (!_log) { return; }
		try { GD.Print(LogPrefix + message); } catch { }
	}

	private void SafeWarn(string message)
	{
		try { GD.PushWarning(LogPrefix + message); } catch { }
	}
}
