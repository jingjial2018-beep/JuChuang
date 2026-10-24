# JNest historical branch archive — 2026-10-24

**Archive only. Do not use this branch as an application development baseline.**

This commit preserves 40 legacy branch tip commits as additional Git parents, besides the first parent (stable).
The tree is copied from stable with this manifest; it does **not** integrate the legacy changes into the stable application source.
Every original tip SHA remains reachable through `archive/legacy-20261024` after legacy branch refs are deleted.
Never merge, cherry-pick wholesale, or force-push this archive branch into `stable`.

Official baseline at archive creation: `v1.0.7`, stable tip `81f7cb388a6812da6fd841d254a5483d5c0ca32d`.
Protected existing branch refs not included in this archive: `stable`, `main`, `chore/repository-cleanup-20261024`, `release/v1.0.3-rc7-wechat-singleclick`, `fix/rc8-recover-showna2`, `fix/v1.0-wechat-relogin-identity`, `feature/notification-branding-next-beta`, `feature/external-tray-activity-monitor`.

| Former branch | Archived commit SHA |
|---|---|
| `architecture/account-window-state` | `77fd858308668a8675e2f2532e59f6887777812a` |
| `beta/v0.3.33-native-caption-guard` | `89e58d326e3b778743cae1efc974c3753a0c1cb5` |
| `beta/v0.3.34-client-area-rounded-beta6` | `60e0e51b000d861ebe6788588712f4836e3cfc84` |
| `beta/v0.3.34-client-panel-rounded-beta7` | `c9d73f9db8d9730d5e1aef2b0cc9fed098d0af8f` |
| `beta/v0.3.34-whatsapp-snap-hover` | `5298db3ee547f03eb86536c07598d4d18e50fdce` |
| `beta/v0.3.34-workspace-chamfer` | `8957f78bbac78f06f930ff76f388c4b83559d5c0` |
| `beta/v0.3.34-workspace-rounded` | `158c12a00cb9527eaff1c102fc429e47134218a1` |
| `beta/v0.3.34-workspace-shell-radius` | `0cc6b2e1e00de7c5e016167642431f619c29e52a` |
| `beta/v1.0-identity-beta5` | `153483b3062af8705743affec0da877eac2d08b4` |
| `codex/external-wechat-activity-preview` | `c687f7eaad98fc49ccad9f602b8a2bac98b99d54` |
| `codex/fix-background-alerts-v0.3.20` | `52189a5d0a9d8a29d9e3c5bfa574653f95c45a61` |
| `codex/taskbar-unread-overlay-v0.3.21` | `4438ed43e8ab883c17258f3faaa96e16be0afdde` |
| `dev/from-v0.3.34-stable` | `002aad86d89361b6ccc9cf0e25f1f164530b5808` |
| `docs/readme-latest-stable-v1.0.2` | `828c79145e0fdaf7b75462c76a9a309074df082e` |
| `docs/readme-seo-geo` | `197881dfdb7f256d232af83a08fe3844a3170736` |
| `experiment/recessed-shell-beta2` | `8e8d8f54314d03ccd8b886246da7b08634a50d5b` |
| `feature/notification-branding-beta2` | `57ef4754d11e8071825d216db302ffce9cbcdb86` |
| `feature/v1.0.6-sidebar-rightclick-guard` | `ce371e99dbe903c95d3da8c758bbfb7db203f435` |
| `feature/v1.0.6-stable-settings-font` | `a188bfa902691debe738b9ae93305a01fc0baf9f` |
| `fix/minimize-taskbar` | `6d7763c12cf12d1e787bfefaac78a7f259a77722` |
| `fix/rc2-about-settings-frontonly` | `ff41305957153f4a40111999e2b8c8e47dd177d8` |
| `fix/scan-clients-discovery` | `0c293e6c3773344072bf71894bb7dbd898e6c29e` |
| `fix/taskbar-icon-beta1` | `efbce65fbebf3fea66cb444e7aa02914a2f25e81` |
| `fix/taskbar-icon-v0.3.22` | `508f68b9ac353fa3d276927f96164cc9430d1b0f` |
| `fix/v1.0.3-rc6-reconnect-restore-settle-test` | `ae1b67f141f044b1a5cb65f8d023fb68e66309c1` |
| `fix/v1.0.3-rc6-reconnect-size-preserve` | `fafbaf224ee7bc253dca74b2c7d8a5af4a9501a3` |
| `release/v0.3.15-authenticode` | `d4f85ec3d453fca0ad8633aeb40cd90fa4849712` |
| `release/v0.3.15-unsigned` | `2f58d678c6ed48adf454b07f7861cbff74a35d31` |
| `release/v0.3.16` | `4cfb218460889b70cb7025f371b08fdff5e30c61` |
| `release/v0.3.17-stage` | `62ddd09e538575894834be3197d426152a76f70b` |
| `release/v0.3.18` | `239a56807c3c902d53d8aae36888d837ef2db2df` |
| `release/v1.0.3-exit-dialog-final` | `02fc234441b3139382279aced46847d7c6fe9264` |
| `release/v1.0.4-beta.4-stability` | `0ae968591460547bebe5cf50e4ef6dee4d4ba4c4` |
| `stability/v0.3.13` | `4a1738b1aa577b32b5ba85d7e664ce549207f8ec` |
| `test/v1.0.5-beta.2-menu-icon-tune` | `b9644c5986e53abc756347f121bc9d949532ccfe` |
| `test/v1.0.5-settings-spacing` | `860eaf3ba559c6e6184378be0db3bed1c60aa7ae` |
| `ui/page-shell-v0.3.12` | `5f6ebb80034a346606174b1c9fc856feb846d09a` |
| `ui/sogou-flat-shell` | `6f5845f007e28844f00544dc5667966ed0023408` |
| `ui/v0.3.14-polish` | `210f9dbfad08af86a1f71a63d8deb387b5e853d7` |
| `ui/v0.3.15-toolbar-right` | `d56fa1499b540d18b0b7fca201ead6cde830220c` |

To recover a former branch: `git switch -c <new-name> <archived-commit-sha>`; never create a new working branch from the archive commit itself.

This is a structural archive for historical reachability, not a checked-out code backup and not an approved feature merge.
