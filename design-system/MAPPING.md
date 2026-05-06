# Design System Token ↔ WPF Brush Mapping

This file is the bridge between the design system's CSS tokens (`design-system/colors_and_type.css`) and the WPF brushes / resources defined in [`WpfApp/Themes/CustomStyles.xaml`](../WpfApp/Themes/CustomStyles.xaml).

**The CSS file is the source of truth.** New tokens go there first. The WPF resources mirror them with the `Dd*` prefix (e.g. `--dd-accent` ↔ `DdAccent`).

WPF color values use ARGB hex (`#AARRGGBB`); CSS uses `rgba()` or hex without alpha. The conversion: alpha-percent × 255 → hex byte, prepended (e.g. `0.88 × 255 = 224 = 0xE0`).

---

## Colors

### Backgrounds (glass backdrop)

| CSS token | CSS value | WPF brush | WPF color |
|---|---|---|---|
| `--dd-bg-base` | `#0A1520` | `DdBgBase` | `#FF0A1520` |
| `--dd-bg-glass` | `rgba(10,21,32,0.88)` | `DdBgGlass` | `#E00A1520` |
| `--dd-bg-sidebar` | `rgba(0,0,0,0.56)` | `DdBgSidebar` | `#8F000000` |
| `--dd-bg-header` | `rgba(0,0,0,0.31)` | `DdBgHeader` | `#4F000000` |
| `--dd-bg-overlay` | `rgba(0,0,0,0.50)` | `DdBgOverlay` | `#80000000` |

### Surfaces (white-on-glass)

| CSS token | CSS value | WPF brush | WPF color |
|---|---|---|---|
| `--dd-surface-1` | `rgba(255,255,255,0.05)` | `DdSurface1` | `#0DFFFFFF` |
| `--dd-surface-2` | `rgba(255,255,255,0.08)` | `DdSurface2` | `#14FFFFFF` |
| `--dd-surface-3` | `rgba(255,255,255,0.12)` | `DdSurface3` | `#1FFFFFFF` |
| `--dd-surface-row` | `rgba(255,255,255,0.06)` | `DdSurfaceRow` | `#0FFFFFFF` |

### Borders

| CSS token | CSS value | WPF brush | WPF color |
|---|---|---|---|
| `--dd-border-thin` | `rgba(255,255,255,0.10)` | `DdBorderThin` | `#1AFFFFFF` |
| `--dd-border-med` | `rgba(255,255,255,0.18)` | `DdBorderMed` | `#2EFFFFFF` |
| `--dd-border-strong` | `rgba(255,255,255,0.28)` | `DdBorderStrong` | `#47FFFFFF` |

### Text

| CSS token | CSS value | WPF brush | WPF color |
|---|---|---|---|
| `--dd-fg-1` | `rgba(255,255,255,0.95)` | `DdFg1` | `#F2FFFFFF` |
| `--dd-fg-2` | `rgba(255,255,255,0.65)` | `DdFg2` | `#A6FFFFFF` |
| `--dd-fg-3` | `rgba(255,255,255,0.40)` | `DdFg3` | `#66FFFFFF` |
| `--dd-fg-on-accent` | `#1a0a02` | `DdFgOnAccent` | `#FF1A0A02` |

### Brand accent (Material DeepPurple)

| CSS token | CSS value | WPF brush | WPF color |
|---|---|---|---|
| `--dd-accent` | `#673AB7` | `DdAccent` | `#FF673AB7` |
| `--dd-accent-hi` | `#9575CD` | `DdAccentHi` | `#FF9575CD` |
| `--dd-accent-lo` | `#512DA8` | `DdAccentLo` | `#FF512DA8` |
| `--dd-accent-soft` | `rgba(103,58,183,0.22)` | `DdAccentSoft` | `#38673AB7` |
| `--dd-accent-grad` | linear-gradient(180deg, #9575CD 0%, #673AB7 55%, #512DA8 100%) | `DdAccentGrad` | LinearGradientBrush (vertical, 3 stops) |

### Secondary accent (Material Indigo)

| CSS token | CSS value | WPF brush | WPF color |
|---|---|---|---|
| `--dd-info` | `#7986CB` | `DdInfo` | `#FF7986CB` |
| `--dd-info-soft` | `rgba(121,134,203,0.20)` | `DdInfoSoft` | `#337986CB` |

### Semantic

| CSS token | CSS value | WPF brush | WPF color |
|---|---|---|---|
| `--dd-success` | `#23C46A` | `DdSuccess` | `#FF23C46A` |
| `--dd-success-soft` | `rgba(35,196,106,0.18)` | `DdSuccessSoft` | `#2E23C46A` |
| `--dd-warn` | `#FFB547` | `DdWarn` | `#FFFFB547` |
| `--dd-danger` | `#FF6B6B` | `DdDanger` | `#FFFF6B6B` |
| `--dd-danger-soft` | `rgba(255,107,107,0.16)` | `DdDangerSoft` | `#29FF6B6B` |
| `--dd-neutral` | `#808890` | `DdNeutral` | `#FF808890` |

### Keyboard-view specific

| CSS token | CSS value | WPF brush | WPF color |
|---|---|---|---|
| `--dd-key-bg` | `rgba(255,255,255,0.03)` | `DdKeyBg` | `#08FFFFFF` |
| `--dd-key-border` | `rgba(255,255,255,0.10)` | `DdKeyBorder` | `#1AFFFFFF` |
| `--dd-key-hover` | `rgba(149,117,205,0.12)` | `DdKeyHover` | `#1F9575CD` |
| `--dd-key-selected` | `rgba(149,117,205,0.26)` | `DdKeySelected` | `#429575CD` |
| `--dd-key-text` | `rgba(255,255,255,0.85)` | `DdKeyText` | `#D9FFFFFF` |
| `--dd-key-text-dim` | `rgba(255,255,255,0.45)` | `DdKeyTextDim` | `#73FFFFFF` |
| `--dd-actuation-low` | `#7986CB` (Indigo 300) | `DdActuationLow` | `#FF7986CB` |
| `--dd-actuation-mid` | `#B39DDB` (DeepPurple 200) | `DdActuationMid` | `#FFB39DDB` |
| `--dd-actuation-high` | `#9575CD` (DeepPurple 300) | `DdActuationHigh` | `#FF9575CD` |

These are added now (in plan 1) so the plan-2 keyboard view can be built without re-touching `CustomStyles.xaml`.

---

## Type scale

WPF resources are `<sys:Double>` keys (so they can be used as `FontSize="{StaticResource DdFsBase}"`).

| CSS token | Size (px) | WPF resource |
|---|---|---|
| `--dd-fs-xs` | 10.5 | `DdFsXs` |
| `--dd-fs-sm` | 11.5 | `DdFsSm` |
| `--dd-fs-base` | 12.5 | `DdFsBase` |
| `--dd-fs-md` | 13.5 | `DdFsMd` |
| `--dd-fs-lg` | 15.5 | `DdFsLg` |
| `--dd-fs-xl` | 20 | `DdFsXl` |
| `--dd-fs-xxl` | 26 | `DdFsXxl` |

The 15 distinct font sizes currently inlined across the WPF XAML files collapse to these 7. See migration commits (titlebar / sidebar / tabs / dialogs) for the 1:1 replacement table per file.

### Line heights, letter spacing

| CSS token | Value | WPF resource | Notes |
|---|---|---|---|
| `--dd-lh-tight` | 1.2 | n/a | WPF uses `LineHeight` per-control, not a global token |
| `--dd-lh-base` | 1.45 | n/a | same |
| `--dd-lh-relaxed` | 1.6 | n/a | same |
| `--dd-tracking-eyebrow` | 0.10em | n/a (drift) | WPF doesn't natively support letter-spacing on TextBlock; can fake via `Typography.Capitals` or per-character spacing — flagged as a minor visual drift on eyebrows ("PROFILES", "ACTUATION", etc.) |
| `--dd-tracking-brand` | 0.16em | n/a (drift) | Same — affects "DRUNKDEER CONTROL" wordmark |
| `--dd-tracking-tight` | -0.005em | n/a (drift) | Negligible visual effect; safe to skip |

---

## Font families

| CSS token | CSS value | WPF resource | WPF FontFamily |
|---|---|---|---|
| `--dd-font-sans` | `'Inter', -apple-system, ..., 'Segoe UI', system-ui, sans-serif` | `DdFontSans` | `Inter, Segoe UI Variable, Segoe UI` |
| `--dd-font-mono` | `'JetBrains Mono', 'Consolas', ..., monospace` | `DdFontMono` | `JetBrains Mono, Cascadia Mono, Consolas` |
| `--dd-font-display` | `'Inter', sans-serif` | (use `DdFontSans`) | n/a — no separate display token in WPF |

**Known drift:** Inter is not bundled in v1. Windows 11's `Segoe UI Variable` is the fallback (close visual proxy). To bundle Inter properly, ship the TTFs in `WpfApp/Resources/Fonts/` and reference via pack URI (e.g. `pack://application:,,,/Resources/Fonts/#Inter`). Same applies to JetBrains Mono → falls back to `Cascadia Mono` (shipped with Windows Terminal) → `Consolas`. Bundling fonts is a follow-up; not in plan-1 scope.

---

## Radii

| CSS token | Value (px) | WPF resource | Notes |
|---|---|---|---|
| `--dd-radius-xs` | 4 | `DdRadiusXs` | `<CornerRadius x:Key="DdRadiusXs">4</CornerRadius>` |
| `--dd-radius-sm` | 6 | `DdRadiusSm` | keycap, small button |
| `--dd-radius-md` | 8 | `DdRadiusMd` | list item, menu |
| `--dd-radius-lg` | 10 | `DdRadiusLg` | card |
| `--dd-radius-xl` | 12 | `DdRadiusXl` | window outer, dialog |
| `--dd-radius-pill` | 999 | `DdRadiusPill` | status pills, primary CTA |

---

## Shadows (DropShadowEffect)

| CSS token | CSS value | WPF resource |
|---|---|---|
| `--dd-shadow-1` | `0 1px 2px rgba(0,0,0,0.30)` | `DdShadow1` — BlurRadius=2, ShadowDepth=1, Opacity=0.30 |
| `--dd-shadow-2` | `0 4px 14px rgba(0,0,0,0.35)` | `DdShadow2` — BlurRadius=14, ShadowDepth=4, Opacity=0.35 |
| `--dd-shadow-3` | `0 12px 32px rgba(0,0,0,0.45)` | `DdShadow3` — BlurRadius=32, ShadowDepth=12, Opacity=0.45 |
| `--dd-shadow-key` | `inset 0 -1px 0 rgba(0,0,0,0.45), inset 0 1px 0 rgba(255,255,255,0.05)` | n/a (drift) — WPF can't paint inset shadows on a Border. Build-up via inner Borders if needed by keycap. |
| `--dd-glow-accent` | `0 0 0 1px rgba(149,117,205,0.55), 0 4px 18px rgba(103,58,183,0.40)` | `DdGlowAccent` — `DropShadowEffect BlurRadius=18 ShadowDepth=4 Color=#673AB7 Opacity=0.40`. The 1px ring is approximated via a solid Border with `BorderBrush={Binding ...}` — flagged as approximation. |

---

## Motion

WPF storyboards take `KeyTime`/`Duration` as TimeSpan, and easing functions are objects (`CubicEase`, `QuadraticEase`, etc.). The CSS bezier `(0.22, 0.61, 0.36, 1.00)` is not directly representable; closest match is `CubicEase` with `EasingMode.EaseOut`.

| CSS token | CSS value | WPF mapping |
|---|---|---|
| `--dd-ease-out` | `cubic-bezier(0.22, 0.61, 0.36, 1.00)` | `CubicEase EasingMode=EaseOut` |
| `--dd-ease-in-out` | `cubic-bezier(0.65, 0.05, 0.36, 1.00)` | `CubicEase EasingMode=EaseInOut` |
| `--dd-dur-fast` | 120ms | `0:0:0.12` |
| `--dd-dur-base` | 180ms | `0:0:0.18` |
| `--dd-dur-slow` | 260ms | `0:0:0.26` |

Define these once as resources in `CustomStyles.xaml` so storyboards can reference them.

---

## Spacing

The CSS spacing scale is broader than the user's stated "4 / 8 / 12 / 16 / 22 / 32" — the actual canonical scale (used as the source of truth for migration) is:

| CSS token | Value (px) |
|---|---|
| `--dd-space-1` | 4 |
| `--dd-space-2` | 6 |
| `--dd-space-3` | 8 |
| `--dd-space-4` | 10 |
| `--dd-space-5` | 14 |
| `--dd-space-6` | 16 |
| `--dd-space-7` | 20 |
| `--dd-space-8` | 24 |
| `--dd-space-9` | 32 |
| `--dd-space-10` | 48 |

WPF doesn't have a "space token" type — `Margin` and `Padding` take inline numbers (`Thickness`). We don't define resources for these; we just snap inlined values to the scale during migration. Off-scale outliers in the current XAML and their snap targets:

- 5 → 4
- 7 → 8
- 18 → 16
- 22 → 24
- 50 → 48
- 64 → 48 (or stay if it's a fixed positional offset)

Already on-scale values (4, 6, 8, 10, 12, 14, 16, 20) stay unchanged.

---

## Known drifts (acceptable for v1)

1. **Inter font not bundled** — falls back to Segoe UI Variable. Documented above.
2. **JetBrains Mono not bundled** — falls back to Cascadia Mono → Consolas.
3. **Letter spacing on uppercase eyebrows** — WPF doesn't natively support CSS-style `letter-spacing`. Acceptable visual drift on text like "PROFILES" / "ACTUATION".
4. **Inset shadow on keycap** (`--dd-shadow-key`) — WPF can't paint inset shadows. The keycap UserControl in plan 2 will fake it via inner Borders if the look matters.
5. **Cubic-bezier eases** — WPF's `CubicEase` is not exactly the same curve as the design system's bezier. Visually indistinguishable for the durations involved (120–260ms).
6. **`AccentSoft` blue → `DdAccentSoft` purple** — the existing brush was blue (`#906AB0FF`); replacement aligns with `App.xaml`'s `PrimaryColor="DeepPurple"`. Visible color shift on profile pill and any element using the legacy soft accent.
7. **Notification dot orange → indigo** — the existing options-gear dot was `#FFE5732B`; replacement is `DdInfo` (`#7986CB`), matching the design system's "Update Available" guidance.
