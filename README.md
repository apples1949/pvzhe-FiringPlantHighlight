# 发射位高亮（FiringPlantHighlight）

> Mod ID：`firingplanthighlight` · 版本：**1.0.2** · 作者：apples1949
> 适用基线：**0.33.0.0**（0.33 解包源码 + `data_PlantsVsZombies_windows_x86_64` 编译引用）
> 产物：`dist/FiringPlantHighlight.pmod`（schemaVersion 2 / runtimeApiVersion 3）

## 它做什么

加农炮类植物「点开 → 选发射位置」的时候，**场上所有同类植物一起持续闪「深红色的光」**
（颜色可在配置里改，默认 `#8B0000` 深红），一眼就能看出还有哪几株在待命、哪一株正在瞄准；
**设好发射位 / 右键取消 / 植物死亡 / 退出战斗后立刻恢复原样**，亮度与颜色都归零，绝不残留染色。

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
   - **闪光颜色**（默认 `#8B0000` 深红；改成纯白就是游戏原生的「悬停变亮」那种白光）
     - 即时生效：改完立刻换色，不用重启、不用重新进选发射位
     - A（不透明度）当光强比例：1 = 满强度，0.4 = 40% 强度
   - 输出诊断日志（默认关）

## 实现要点（为什么安全）

- **状态判据只认宿主自己的字段**：`MousePressComponent.IsPressed`（`ToggleMode` 植物按下去 = 正在
  选发射位）。选好（`FinishAim`）/ 右键取消（`CancelAim`）/ 植物死亡（`OnAliveChanged`）/ 组件卸载
  （`OnDetaching`）宿主都会把它清成 `false` ⇒ 我们不需要自己枚举取消路径，也就不会「漏取消」。
- **色光复用宿主自己的渲染色通道**：`AdobeAnimateSprite.SetRenderColorMultiplier(Color)`（0.33 源码
  `addons/AdobeAnimateEditor/Node/AdobeAnimateSprite.cs:2605`，public）。这正是宿主实现
  「鼠标悬停变亮 / 受击闪白」的同一个函数（`ShaderEffectComponent.cs:504-516` 里
  `SetRenderColorMultiplier(new Color(1+n,1+n,1+n))`），我们只是把纯白系数换成本 Mod 算出的**带色**系数。
  不新增贴图、不加 Node、不写 shader、不改材质文件、不改任何判定与数值。
  - 0.33 的角色 shader（`ShaderTowerDefenseCharacter.gdshader`）**没有可 per-instance 写的颜色 uniform**：
    `brightStrength`(L8)/`whiteStrength`(L9) 只有亮度，染色是 `effectFlags` 位预置色（L2-6/L17-26），
    唯一的 `vec4 modulate`(L43) 是**材质级** uniform，写它会把所有角色一起染色。
  - 颜色公式：`m_c = 1 + 强度·k·(2·fc_c/max(fc) − 1)`，`k = max(fc)`。
    白色 ⇒ `(1+b,1+b,1+b)`，与 v1.0.1 的 `brightStrength` **逐值等价**；
    深红 `#8B0000` ⇒ 峰值 `(1.27, 0.73, 0.73)`（红提亮、绿蓝压暗）= 深红色的光。
    颜色极暗/接近中性导致幅度看不见时，自动退化成纯亮度闪。
- **不碰别的染色**：咖啡豆 `blink`（effectFlags 位 256）本 Mod 从不清零；模仿者灰化是
  `SetRenderGrayscale`（与颜色乘算两个独立字段）也不碰；冰霜/魅惑/红热/中毒/灰烬/全息这些
  「游戏自己上的色」在**进入前**就用 `ShaderEffectComponent.GetEffectFlags()` 检测并**跳过该株**。
- **退出只复原自己动过的**：逐株调宿主自己的 `ShaderEffectComponent.RefreshShaderNodes()`
  （`ShaderEffectComponent.cs:192-250`，public）按它当前真实状态重算亮度/灰化 —— 悬停变亮、
  受击闪白都能原样回来；组件已释放时兜底写回 `Colors.White`（宿主自己的「无色」）。
  另有每帧兜底：不需要闪的那一帧、植物死亡、被移除、关总开关、离开战斗都立刻复原。
- 不写存档、不改配置结构、不联网、不监听按键。

## 已知限制 / 未验证项

- **未实机验证**（本轮硬约束：不启动游戏、不装机）。已完成的验证见下方「验证状态」。
- 若玩家在「瞄准中」直接点选卡栏拿卡，游戏自己会保留瞄准态（0.33 新增
  `TowerDefenseManager.IsPointerInPacketBankArea` 的提前 return），本 Mod 与游戏保持一致：
  继续闪，直到游戏自己把 `IsPressed` 清掉。这是「跟随宿主状态」的取舍，不是残留。
- 与「受击闪白 / 悬停变亮」共用同一条渲染色通道：植物正在闪色光时被打或被鼠标悬停，那一两帧会显示
  宿主的白闪/白亮（宿主的写入覆盖我们），之后自动回到色光；属于视觉优先级问题，不会出现错误颜色。
- 受「进入前已被游戏染色就跳过」这条安全规则影响：正在冰霜/魅惑/中毒/模仿者灰化/灰烬状态下的植物
  **不会**被染上色光（避免把游戏自己的颜色压掉）。
- 网络对局：只改本机渲染，不同步、不影响判定；非房主也能看到自己的高亮。

## 验证状态（本轮）

| 项目 | 结果 |
|---|---|
| 编译 `python mods/_build_locked.py FiringPlantHighlight` | **RC=0**，0 warning / 0 error |
| `verify_pmod.py dist/FiringPlantHighlight.pmod` | **FAIL=0**（ok=9, warn=0） |
| DLL 特征串 | `color` / `#8B0000` / `renderColorMultiplier` / `v1.0.2` / `闪光颜色=` / `SetRenderColorMultiplier` / `RefreshShaderNodes` / `MousePressComponent` / `ToggleMode` / `IsPressed` 全部命中；`brightStrength` 已不再出现（不再占用宿主亮度别名） |
| 实机功能 | **未验证**（不启动游戏） |

## 目录

```
mods/FiringPlantHighlight/
├─ mod.json                              # schemaVersion 2 / id firingplanthighlight / v1.0.2
├─ runtime_src/FiringPlantHighlightEntry.cs
├─ runtime_src/FiringPlantHighlight.csproj
├─ build_and_install.py                  # 只编译（--install 才打包装机）
├─ build_pmod.py                         # 编译产物 → dist/FiringPlantHighlight.pmod
├─ Runtime/ModAssembly.dll               # 包内托管程序集（身份名 JTYFiringPlantHighlight）
└─ dist/FiringPlantHighlight.pmod
```
