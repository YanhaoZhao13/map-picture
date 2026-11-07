# map-picture

将原始旅行摄影制作成 Editorial Photography / Travel Zine 风格的地理视觉作品。地图采用独立透明图层，保留照片完整画幅、原生分辨率与色彩。

## 使用

将本目录复制到 Codex 的用户技能目录 `~/.codex/skills/map-picture`，然后上传原图并说明拍摄地点：

> 用 $map-picture 处理这张照片，拍摄地点：崂山仰口。

技能由 agent 读取 `SKILL.md` 后执行。它不是一键滤镜：地点核实、真实地图下载、候选构图比较与最终视觉检查需要 agent 完成。

## 能力

- 根据城市、海滨、山地选择真实路网、水系、海岸线和地形数据。
- 根据可用天空与摄影主体辅助布局；比较主体平衡、留白和文字可读性。
- 分别控制地图核心对比度、主次层级及外围自然渐隐。
- 输出无损成品、透明地图图层、预览及逐像素核验记录。

## 本地渲染器

附带脚本适用于 Windows PowerShell 5.1 和 System.Drawing。示例配置见 `examples/beijing-school.json`；原片和 OSM 数据需自行提供。相对输入路径以配置文件所在目录解析。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/render.ps1 -Config examples/beijing-school.json
```

详细配置与限制见 [运行说明](references/runtime.md)。当前自动天空评分是颜色与纹理启发式，需要看图复核。附带渲染器尚无通用旋转参数、自动 DEM 下载或等高线提取功能；这些步骤需要额外实现。macOS/Linux 未完成运行验证。

## 数据与隐私

仓库不包含私人照片、照片 GPS、下载的地图缓存或账户凭据。用户提供的摄影版权属于其权利人。使用 OpenStreetMap 或高程数据时，需遵守各数据源许可并保留署名；技能代码的公开不替代地图数据许可。

## 版本 1.2.0

本版加入主体视觉重量与候选构图比较，并增加核心对比度的预览验收。主路、水岸、等高线与建筑应保持层级，边缘透明度单独控制。
