# 发射位高亮（FiringPlantHighlight）

> 选择发射位置时，场上所有「有发射职能」的植物持续闪光，一眼看清哪些植物在等你下指令。
> 选好发射位或取消后立刻恢复原样。

- Mod id：`firingplanthighlight`
- 显示名：发射位高亮
- 版本：1.0.0
- 作者：apples1949
- 包：`dist/FiringPlantHighlight.pmod`

## 一、做了什么

加农炮类植物（玉米加农炮、毁灭玉米炮、冰玉米炮、大蒜玉米炮、豌豆炮、南瓜炮、土豆玉米炮、
大喷菇·炮、玉米坚果、仙人掌防空炮、狂野地雷炮，以及**毁灭磁力菇**这种"含发射职能"的植物）
不是自动开火，而是**两次点击**：先点植物进入「选择发射位置」，再点地面目标格开火。

本 Mod 在「选择发射位置」这一态里，把场上**所有**这类植物置为**持续闪光**；
一旦选好目标格（开火）、松手取消、点非地面格取消、植物死亡、切场景 / 重开，
闪光**立即**停止并恢复原样。

## 二、怎么玩

1. 进任意有加农炮类植物的关卡，种下若干株。
2. 点其中一株 → 进入「选择发射位置」：场上所有这类植物立刻开始闪光。
3. 点地面目标格开火，或松手 / 点非地面格取消 → 闪光立即停止。
4. 战斗中关掉「启用发射位高亮」开关，也会立刻清掉正在闪光的状态。

## 三、配置项

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `enabled` | bool | `true` | 启用发射位高亮。关 = 完全不动，植物保持游戏原生外观。 |
| `enableLog` | bool | `false` | 输出诊断日志（进入 / 退出选择态、高亮对象数量写进 `godot.log`）。 |

## 四、实现要点（为什么这样做）

| 关注点 | 结论 | 0.33 源码依据 |
|---|---|---|
| 「正在选发射位」判据 | `MousePressComponent.IsPressed == true` | `Script/Component/TowerDefense/Character/MousePressComponent/MousePressComponent.cs:79`（进入 :400-402，完成 :422-426，取消 :458-461，死亡 :129-136 → :669） |
| 「含发射职能」判据 | 植物挂了 `CannonComponent` | `Script/Component/TowerDefense/Character/CannonComponent/CannonComponent.cs:6`（开火入口 :734） |
| 「一直闪光」实现 | 复用游戏自带 `EFFECT_BLINK` 效果位（=256） | `Asset/Shader/TowerDefense/ShaderTowerDefenseCharacter.gdshader:25,230-233`；写入口 `Prefab/TowerDefense/Character/TowerDefenseCharacter.cs:3736-3742`；原生用法 `Resource/TowerDefense/Character/Buff/TowerDefenseCharacterBuffCoffee.cs:48,61,79,84,94` |
| 全场枚举 | `TowerDefenseManager.Instance.characterRegistry.GetCharactersIntersectingRectList(...)` | `Core/TowerDefenseManager/TowerDefenseManager.cs:142`（同款写法：`mods/Glove/runtime_src/GloveEntry.cs:1720-1760`） |

**不造 shader**：直接把角色 shader 的 `effectFlags` 里的 blink 位打开 / 关掉，
与咖啡豆 buff「到期前闪烁」完全同一套渲染路径，不会有自写 shader 的兼容风险。

**不抢别的闪烁**：进入前 blink 位本来就是 1 的植物（咖啡豆 / 禁忌豆 buff 正在闪）
**不接管、不记录**，退出时只关自己打开的那些。

**零残留**：每帧对账（`process_frame`），`IsPressed` 一旦变 false 就还原；
植物死亡 / 关卡结束 / 注册表取不到 / 配置关掉 / Mod 卸载（`Shutdown`）都会走同一条还原路径。

## 五、构建

```bash
# 编译（串行锁，多 Agent 并行安全）
python mods/_build_locked.py FiringPlantHighlight

# 打包
python mods/FiringPlantHighlight/build_pmod.py

# 离线校验（必须 FAIL=0）
python pvz-hybrid-mod-skills/tools/pmod-toolchain/verify_pmod.py mods/FiringPlantHighlight/dist/FiringPlantHighlight.pmod
```

- 编译引用：`data_PlantsVsZombies_windows_x86_64\`（0.33 的 `GodotSharp.dll` / `PlantsVsZombies.dll`）。
- 程序集身份名：`JTYFiringPlantHighlight`（安卓要求全局唯一），打包时改名为 `ModAssembly.dll`。

## 六、目录

```
mods/FiringPlantHighlight/
├─ mod.json                       # schemaVersion 2
├─ build_and_install.py           # 编译（--install 才打包+装机；本 Mod 未装机）
├─ build_pmod.py                  # 打包 dist/FiringPlantHighlight.pmod
├─ runtime_src/
│  ├─ FiringPlantHighlight.csproj
│  └─ FiringPlantHighlightEntry.cs
├─ Runtime/ModAssembly.dll        # 打包时由 bin/Release 覆盖
└─ dist/FiringPlantHighlight.pmod
```

## 七、未验证项

- 静态（编译 RC=0 + `verify_pmod` FAIL=0 + DLL 特征串）已确认；**游戏内实际观感未验证**（本轮不启动游戏、不装机）。
- 需要人工在 0.33 实机确认：闪光亮度/频率是否足够醒目、与咖啡豆 buff 同时存在时是否互不干扰。
