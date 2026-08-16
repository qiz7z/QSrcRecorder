# QSrcRecorder 设计系统

由 `ui-ux-pro-max` 技能的 `design_system.py` 生成（查询：screen recorder utility desktop tool modern clean），
桌面端适配后落地。token 实现在 `src/ScreenRecorder/UI/Theme.cs`，界面代码只引用 token。

## 风格

**Minimalism & Swiss Style**：干净、留白、高对比、几何网格、去装饰。
桌面字体映射：Poppins → Segoe UI Semibold（标题），Open Sans → Segoe UI（正文）。

## 配色（技能输出："Recording red + waveform blue"）

| Token | 值 | 用途 |
|---|---|---|
| Surface | `#FFFFFF` | 页面底（Swiss 纯白） |
| Container | `#FFFFFF` | 卡片 |
| BorderSubtle | `#FAE4E4` | 卡片描边（暖） |
| BorderStrong | `#E2E8F0` | 输入框描边 |
| TextPrimary | `#0F172A` | 主文字 |
| TextSecondary | `#475569` | 次要文字/标签 |
| TextTertiary | `#94A3B8` | 辅助提示 |
| Brand | `#DC2626` | 录制红：主按钮/停止/Logo——只用于录制动作 |
| BrandHover | `#B91C1C` | 红·悬停 |
| BrandSubtle | `#FCF1F1` | 红·弱化底（兼输入框底） |
| Accent | `#2563EB` | 波形蓝：选中态/链接/下拉高亮 |
| AccentHover | `#1D4ED8` | 蓝·悬停 |
| AccentSubtle | `#EFF6FF` | 蓝·浅底（选中卡片） |

语义纪律：**红=录制操作，蓝=选择/导航**，两者不混用。

## 字阶（12/13/14/16/20，Segoe UI）

标题 600+ / 正文 400 / 标签 500；时间数字 Consolas 等宽。

## 间距与圆角

8pt 网格：4/8/12/16/24。控件高 30，主按钮 52。
圆角三档：输入 6 / 图标块 8 / 卡片 10。

## 海拔

Swiss 风格：**不用投影**，用暖色细描边区分卡片（技能建议 sharp shadows if any）。

## 交互状态

- 选中卡片：蓝描边 1.6px + 图标块蓝底反白 + 标题蓝色加粗 + 极浅蓝底
- 悬停：卡片 `#F8FAFC`；主按钮 `#B91C1C`
- 每屏唯一主操作 = 录制按钮（红）

## 无障碍自查

- [x] 正文/背景对比 ≥ 4.5:1（TextPrimary #0F172A on #FFFFFF = 17:1）
- [x] 点击目标 ≥ 30px
- [x] 状态不只靠颜色（描边 + 图标反白 + 字重）
- [x] 等宽时间数字
- [x] 175% DPI 实测无裁字
