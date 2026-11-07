# 本地执行与迁移

## 能力与环境边界

技能中的设计、地理核对、修改方法和验收约束可迁移到支持 `SKILL.md` 的 agent。随附的可运行后端使用 **Windows PowerShell 5.1 + .NET Framework System.Drawing**，无需 Photoshop、付费制图服务或 Python。它支持普通 JPEG/PNG 等 GDI+ 可解码图片；不把 HEIC、HDR、16-bit 或 RAW 静默降位后称为无损保真。

在 Windows 用 `powershell`，不要把它误写为 `pwsh`：PowerShell 7 的程序集环境与此后端不同。脚本相对自身寻找 C# 文件，相对 JSON 配置文件寻找照片、OSM 和遮罩；可从任意工作目录调用。

macOS/Linux、云端 Work 可以读取相同技能，但不能直接执行这个 Windows 后端。需要改用支持该平台的确定性图像/矢量工具（例如 Pillow/ICC 管理、SVG 渲染或 GIS），仍须实现 EXIF 方向、透明图层、原生尺寸和逐像素验收。不要把“技能文件可迁移”说成“所有脚本已经跨平台验证”。

## 一次任务的目录

不要向已安装技能目录写照片、地图、密钥或运行产物。为这次任务创建单独目录：

```text
job/
  source.jpg
  map.osm
  job.json
  optional-allowed-mask.png
  outputs/
```

输入可以位于别处，配置可使用绝对路径。跨机器迁移任务时把路径改成相对路径；只迁移技能时无需携带用户照片。每轮使用新的 `prefix`，如 `travel-v1`、`travel-v2`。

调用示例：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File 'PATH-TO-SKILL/scripts/render.ps1' -Config 'PATH-TO-JOB/job.json'
```

`PATH-TO-SKILL` 和 `PATH-TO-JOB` 是待替换的运行位置。用户通常只需上传原图并说明地点，由 agent 核实地理、下载数据、填写 JSON 和运行，不要求用户自己填写经纬度。

## 配置

从 [../examples/beijing-school.json](../examples/beijing-school.json) 复制。字段说明：

| 字段 | 作用 |
|---|---|
| `source` | 原始图片路径，不能是聊天缩略图 |
| `osm` | 真实 OSM XML 路径 |
| `output_dir`, `prefix` | 导出目录与版本名前缀 |
| `bounds` | `west,south,east,north`，WGS84，决定地图地理范围 |
| `pin` | `lon,lat,label,note`；label 为一行或多行文本数组；note 说明区域定位 |
| `title`, `subtitle` | 城市疏排大写标题与可选地区副标题 |
| `labels` | 少量 `lon,lat,text,dx,dy`；dx/dy 为设计像素偏移 |
| `source_urls` | 核实地点/地图来源的 URL 数组 |
| `layout.mode` | `auto` 或 `manual` |
| `layout.min_area`, `max_area` | 候选地图外接范围占整幅面积，默认 0.15–0.25 |
| `layout.max_bottom` | 候选下缘最多到照片高度的比例，初值约 0.80，可因照片调整 |
| `layout.left`, `top`, `width` | manual 的归一化位置和宽度；高度由地理比例与照片比例推导 |
| `layout.allowed_mask` | 可选允许区灰度图，白色可覆盖，黑色受保护 |
| `layout.clear_mask` | 可选清晰度灰度图，白色优先保留细节，黑色降低墨线，不代表能覆盖主体 |
| `typography` | 可选标题/副标题/地点/地名/备注字号及衬线、无衬线字体 |

遮罩需与**方向修正后的原片**同尺寸、同方向。先看遮罩与照片叠加确认对齐。天空被树冠切断、夕阳、海面负空间等场景可用人工或可靠语义工具提供遮罩；不能只换 `mode=manual` 就认为主体已被正确识别。

所有位置、边界与文字修改都在 config 中，避免复制一份脚本后写死新地点。字号按照片短边缩放，以预览中清晰可读、不过分抢眼为准。

`typography` 的具体键为 `title_size`、`subtitle_size`、`pin_size`、`label_size`、`note_size`、`font_serif`、`font_sans`。尺寸单位是短边 1800 像素时的设计像素，实际像素数乘以 `min(width,height)/1800`；辅助地名 `dx/dy` 使用同一比例。默认标题/副标题/地点/辅助地名/备注为 46/16/22/20/14，字体 Georgia/Arial；字号由 agent 根据实际文字长度与照片调整。`preview_width` 默认 1200，高清成品仍用原生尺寸。`render.ink` 可设置地图墨色，例如 `#F1E8DA`；`render.line_scale` 默认 1，可在 0.25–3 之间调节线宽（同时用于密度评分与最终绘制），调整后仍应检查主次层级而非一味加粗。

脚本导出 `prefix.png`、`prefix-map.png`、`prefix-preview.png`、`prefix-manifest.json`；auto 另有 `prefix-layout-analysis.png`。manifest 记录源/数据 hash、原始方向与尺寸、选择的位置与候选、要素数量、字体回退、文字保留比例、逐像素核验和待复核项。运行缓存位于导出目录的 `work/` 子目录，出现失败时保存 `failure.json`；失败缓存不是已验收成品。

## 自动选位究竟做什么

1. 在小尺寸分析图中，利用蓝色差异、亮度、局部纹理、顶部连通性估计可用天空；同高度亮度差异帮助降低蓝灰厚云误判。
2. 用户/agent 提供的独立遮罩可以覆盖颜色启发式结果。主体保护和“蓝天优先”分别建模。
3. 渲染真实地图密度图，比较多个位置和等比例尺寸。评分作用于实际道路线条、水岸、定位点与文字，不只比较一块空矩形。
4. 限制地图面积、主体覆盖与文字落点；在质量近似的安全候选中保留足够体量。低置信度或没有安全候选应给出明确反馈，不能把地图自动缩成邮票。
5. 输出布局分析图与参数。agent 看最终预览后，允许调整地理范围、遮罩、文字位置和尺寸，再重新渲染。

常见局限：阴天/夕阳不是蓝天；蓝色玻璃、山体或海面可能相似；顶边被树遮住会破坏连通性；低分辨率分析无法保护每一片叶子；长地点名和复杂字体的实际包围框需要检查。评分是启发式置信度，不是经过标注数据集验证的“无云覆盖率”。

## 色彩、方向与验收

渲染器处理 EXIF 1–8 的旋转/镜像，源照片保持不动；分析与合成必须使用同一张修正后的底片。不要使用依赖 DPI 的 `DrawImageUnscaled`；显式指定源像素范围和目标像素矩形。

图层外 RGB 像素相等只能证明当前解码流程没有改图，不能单独证明 ICC 色彩管理正确。输入含特殊 ICC/宽色域、HDR 或高位深时检查 manifest 的色彩说明；若后端不能完整保留其含义，换支持色彩管理的后端，或先明确说明并获得用户对转换的同意。不要默默丢弃 profile 后声称完全保色。

交付前至少确认：原始文件 hash 未变；输出维度与原片正确方向相同；透明层与成图等大；图层 alpha=0 处所有像素完全不变；成图确实由该底片与该层合成。主体是否被覆盖由遮罩及视觉检查共同判断，不能把“照片下半部分未改变”作为所有照片的通用验收。

迁移后可先运行 `powershell -NoProfile -ExecutionPolicy Bypass -File 'PATH-TO-SKILL/scripts/self-test.ps1'`。测试使用临时生成的图像，检查 EXIF 1–8、DPI、alpha 合成及原生遮罩保护，不需要用户照片或网络；它不能代替真实照片的构图评审。

## 常见失败与恢复

| 情况 | 处理 |
|---|---|
| 找不到输入 | 检查 config 所在目录的相对路径；不读取上次用户的照片替代 |
| OSM 缺细节/截断 | 检查数据范围、引用和选中层，补真实数据；不靠生成假路补齐 |
| 无安全天空候选 | 检视分析图，提供更准确允许区遮罩或人工布局；必要时说明构图面积取舍 |
| 黑底地图/方块边 | 检查 RGBA alpha、背景初始化、数据范围及同形羽化 |
| 点/文字被保护遮罩抹掉 | 换候选、改标签排版或地理范围，不能把标记挪到错误地理位置 |
| 成品看似被裁切 | 检查 EXIF 与显式像素绘制、DPI，回到原片重合成 |
| 字体不存在 | 用已安装的相近字体并报告回退，复核字宽、重叠和中文缺字 |

## 迁移与调用

将整个 `map-picture/` 文件夹一起复制，不能只复制 SKILL.md 而漏掉相对引用和脚本。当前公开 Codex 文档列出用户级 `~/.agents/skills/map-picture` 与项目级 `.agents/skills/map-picture`；部分本地环境从 `~/.codex/skills` 发现技能，安装时以实际宿主确认的目录为准，避免同名重复安装。

在 Codex 中显式使用 `$map-picture`；在支持技能选择的 ChatGPT Work 中选择 `@map-picture`。本地 Work 需要能访问该技能与照片，并启用本地工具；云端不会因为另一台电脑已有文件就自动得到它。若列表尚未刷新，重新打开会话/应用后检查，或直接让 agent 读取本地 SKILL.md 并执行。

产品发现位置和可用界面会变化，必要时核实官方 [Build skills](https://learn.chatgpt.com/docs/build-skills) 与 [Work 本地能力](https://learn.chatgpt.com/docs/get-started-with-work)。

## 人工构图覆盖自动建议

自动选位不能判断完整的摄影视觉平衡。先以 auto 取得可用候选，再对照主体与留白尝试位置明显不同的 manual 配置，并生成预览比较。manual 的 left/top/width 为整幅归一化坐标，高度随地理比例推导；仍需满足面积约束和主体保护。复核最终文字位置，因为每次 Evaluate 都可能重新选择标题与地点标签落点。

优先在配置和视觉决策层改进选位；不能仅添加一个固定“右上奖励”便宣称获得自动审美能力。保存简短候选比较记录。当前脚本仍是启发式辅助，尚无语义视觉重量模型；由调用 agent 看图完成审美选择。
