# 发射位高亮（FiringPlantHighlight）

> Mod ID：`firingplanthighlight` · 版本：**1.0.0** · 作者：apples1949
> 适用基线：**0.33.0.0**（0.33 解包源码 + `data_PlantsVsZombies_windows_x86_64` 编译引用）
> 产物：`dist/FiringPlantHighlight.pmod`（schemaVersion 2 / runtimeApiVersion 3）

## 它做什么

加农炮类植物「点开 → 选发射位置」的时候，**场上所有同类植物一起持续闪光**，一眼就能看出
还有哪几株在待命、哪一株正在瞄准；**设好发射位 / 右键取消 / 植物死亡 / 退出战斗后立刻恢复原样**，
绝不残留。

覆盖的植物（判据来自游戏自身组件配置，不是手写白名单）：

| 植物 | 内部 key | 有 CannonComponent | 0.32 是否已有 |
|---|---|---|---|
| 玉米加农炮 | `CobCannon` | 是 | 有 |
| 寒冰加农炮 | `IceCobCannon` | 是 | 有 |
| 毁灭加农炮 | `DoomCobCannon` | 是 | 有 |
| 僵尸加农炮 | `GarCobCannon` | 是 | 有 |
| 豌豆大炮 | `PeaCannon` | 是 | 有 |
| 南瓜加农炮 | `PumpkinCannon` | 是 | 有 |
| 土豆加农炮 | `PotatoCobCannon` | 是 | 有 |
| 毁灭磁力菇 | `MagnetShroomDM` | 是 | 有 |
| 玉米坚果 | `CornNut` | 否（自己接 `OnFinishPressed`） | **0.33 新增** |

> 辣椒大炮菇（`PowShroom`）虽然也有 `CannonComponent`，但它是 **Line 模式「点一下就朝前开火」**，
> 没有「选发射位置」这一步 ⇒ 不在覆盖范围内（判据自然排除，见方案报告）。

## 怎么用

1. 场上种好上面任意一种植物。
2. 按游戏原生操作点其中一株 → 出现瞄准标记（这一步就是「选发射位」）。
3. 此时场上所有同类植物一起闪光；点地面确定、或右键取消，闪光立刻停止。
4. 想调外观：**选项 → Mod 管理 → 发射位高亮 → 配置**
   - 启用发射位高亮（总开关，默认开）
   - 只高亮正在瞄准的植物（默认关 = 同类全部一起闪）
   - 效果：闪烁 / 常亮（默认闪烁）
   - 闪烁频率（1~8 次/秒，默认 3）
   - 亮度上限（5%~50%，默认 50%；50% 就是游戏原生上限）
   - 输出诊断日志（默认关）

## 实现要点（为什么安全）

- **状态判据只认宿主自己的字段**：`MousePressComponent.IsPressed`（`ToggleMode` 植物按下去 = 正在
  选发射位）。选好（`FinishAim`）/ 右键取消（`CancelAim`）/ 植物死亡（`OnAliveChanged`）/ 组件卸载
  （`OnDetaching`）宿主都会把它清成 `false` ⇒ 我们不需要自己枚举取消路径，也就不会「漏取消」。
- **闪光复用游戏自己的高亮通道**：`TowerDefenseCharacter.SetSpriteGroupShaderParameter("brightStrength", v)`
  —— 和「鼠标悬停植物变亮」是**同一个**公开方法、同一个参数（悬停值 0.3、铲子目标高亮 0.5）。
  不新增贴图、不加 Node、不写 shader、不改材质文件、不改任何判定与数值。
- **不碰 `blink` 通道**：那个是「咖啡豆」Buff 的闪烁位，我们清零会误关玩家的咖啡豆效果。
- **退出即复原**：每帧比对，不需要闪光的那一帧立刻写回 0；`Shutdown` 里先复原再拆连接；
  离开战斗（`TowerDefenseManager.IsGameRunning()` 为假）无条件复原。
- 不写存档、不改配置结构、不联网、不监听按键。

## 已知限制 / 未验证项

- **未实机验证**（本轮硬约束：不启动游戏、不装机）。已完成的验证见下方「验证状态」。
- 若玩家在「瞄准中」直接点选卡栏拿卡，游戏自己会保留瞄准态（0.33 新增
  `TowerDefenseManager.IsPointerInPacketBankArea` 的提前 return），本 Mod 与游戏保持一致：
  继续闪，直到游戏自己把 `IsPressed` 清掉。这是「跟随宿主状态」的取舍，不是残留。
- 与「受击闪白」共用 `brightStrength` 通道：植物正在闪光时被打，闪白会被我们的亮度覆盖到下一帧，
  属于视觉优先级问题（两者都是「变亮」，不会出现异常颜色）。
- 网络对局：只改本机渲染，不同步、不影响判定；非房主也能看到自己的高亮。

## 验证状态（本轮）

| 项目 | 结果 |
|---|---|
| 编译 `python mods/_build_locked.py FiringPlantHighlight` | **RC=0**，0 warning / 0 error |
| `verify_pmod.py dist/FiringPlantHighlight.pmod` | **FAIL=0**（ok=9, warn=0） |
| DLL 特征串 | `FiringPlantHighlight` / `brightStrength` / `SetSpriteGroupShaderParameter` / `MousePressComponent` / `ToggleMode` / `IsPressed` 全部命中 |
| 实机功能 | **未验证**（不启动游戏） |

## 目录

```
mods/FiringPlantHighlight/
├─ mod.json                              # schemaVersion 2 / id firingplanthighlight / v1.0.0
├─ runtime_src/FiringPlantHighlightEntry.cs
├─ runtime_src/FiringPlantHighlight.csproj
├─ build_and_install.py                  # 只编译（--install 才打包装机）
├─ build_pmod.py                         # 编译产物 → dist/FiringPlantHighlight.pmod
├─ Runtime/ModAssembly.dll               # 包内托管程序集（身份名 JTYFiringPlantHighlight）
└─ dist/FiringPlantHighlight.pmod
```
