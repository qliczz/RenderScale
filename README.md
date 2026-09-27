# 斯温·渲染比例（RenderScale）

国服卫月 API 15 **实验测试版 v0.2.0-test.1**。目标是调整 3D 场景内部渲染比例，保留当前窗口和 HUD 尺寸。**当前尚未完成游戏内验收。**

## DLSS 版本

此版跟随游戏已经选择的缩放器，不自动切换 FSR/DLSS，不替换或分发 NVIDIA DLL。

| 游戏缩放器 | 本版允许输入的比例 | 验证边界 |
| --- | --- | --- |
| DLSS | 25–100%，可输入小数 | 实验路径；具体比例能否被游戏接受须实测 |
| FSR | 25–400%，受尺寸/像素预算限制 | 保留原有实验路径，仍待游戏内验收 |

**DLSS 高于 100% 尚未实现。** 需要协调 DLSS 输出资源重建与最终缩小，不能直接套用 FSR 的超采样方案。此版会拒绝高于 100% 的 DLSS 请求；不会偷偷改用 FSR。100% 也不代表已确认 DLAA 模式。

例如窗口 **1980×1080**：DLSS 75% 请求场景 1485×810；50% 请求 990×540，再由游戏 DLSS 路径处理。窗口仍保持 1980×1080。插件读取真实输出尺寸，不把 1980 改成 1920。

DLSS 路径调整现有的动态输入比例，保留输出尺寸，不调用 FSR 的纹理销毁/重建、mip 或最终 blit 修正。若游戏拒绝目标比例，插件停止覆盖；不强行扩大 DLSS 缓冲区。

## 安装与首测

1. 解压 `RenderScale.zip` 到固定目录，在卫月开发插件位置添加 `RenderScale.dll`。
2. 游戏设置中先选择 **DLSS**；如果有生效帧率条件，设为始终启用。停用其他渲染比例插件。
3. 加载本插件，输入 `/swr`；若提示无法检测 DLSS 接口，先确认游戏已启用 DLSS，再重载本插件。
4. 先试 `/swr 67`。核对实际场景尺寸、DLSS 成功调用提示、画面、HUD 位置及点击是否正常。
5. 全部正常才点击保留，或输入 `/swr keep`。15 秒内不确认会请求恢复；`/swr off` 随时停止覆盖。
6. 再测 50%、75% 和所需自定义比例。测试中保持场景与相机相同，便于对照。

命令：`/swr` 打开；`/swr 75` 预览；`/swr keep` 保留；`/swr off` 停止。

## 生效检查与恢复

- 显示目标尺寸和从游戏场景管理器读取的实际尺寸；不将计算出的目标冒充实际结果。
- 通过已加载的 `nvngx_dlss.dll` 导出函数观察 NGX D3D11 EvaluateFeature 返回值，原样转发参数、句柄、回调和返回值。
- DLSS 确认要求：尺寸匹配，且本次预览出现至少三次连续成功调用，最近调用距今不超过两秒。DLL 存在、配置已选 DLSS 或旧预览的成功调用均不能替代检测。
- 四秒后仍未达到实际尺寸或未持续检测到 DLSS 成功调用，停止覆盖；确认后也持续监测。
- 检测不是最终画面的证明，也没有直接读取 NGX 的输入/输出资源尺寸；仍须人工检查画面、HUD、运动拖影、切图、GPose 和卸载恢复。
- 固定比例暂时接管动态分辨率运行参数，关闭时请求恢复；若玩家修改相关游戏保存设置，则停止覆盖并采用新设置。
- 每次加载默认关闭，仅保存偏好比例，不调用游戏设置保存接口。
- 首版兼容范围仍限定已核对的国服 `2026.09.15.0000.0000` 和配套结构库。
- 每边限制 64–8192，像素数最多 33,554,432；帧率、显存占用和画面会随比例变化。

预览计时不能修复 GPU 卡死或游戏崩溃；自动停止也只是恢复请求，实际恢复需要验证。截图文件仍采用游戏输出尺寸。测试包不应作为稳定版推送给全部用户。

## 构建与验证

需要 .NET 10 SDK 和国服 API 15 开发库：

```powershell
./scripts/build.ps1 -DalamudHome '你的 XIVLauncherCN/addon/Hooks/dev 目录'
```

测试：`dotnet run --project tests/RenderScale.Tests -c Release`。
只读静态核对：安装 `pefile` 后运行 `py scripts/audit-signatures.py --game '游戏目录/game/ffxiv_dx11.exe'`。

详细记录见 [VALIDATION.md](docs/VALIDATION.md) 和 [待执行验收表](docs/ACCEPTANCE.md)。

## 来源

渲染后端来自 0x0ade CustomResolution，线程/渲染工具来自 FloppyUtils，遵循 AGPL-3.0；保留来源与固定版本在 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

NGX 导出函数签名和成功码按 NVIDIA 官方 [nvsdk_ngx.h](https://github.com/NVIDIA/DLSS/blob/main/include/nvsdk_ngx.h) 与 [nvsdk_ngx_defs.h](https://github.com/NVIDIA/DLSS/blob/main/include/nvsdk_ngx_defs.h) 核对。仅使用公开 ABI 定义，没有复制或分发 NVIDIA 实现或 DLL。
