# Changelog

## [1.0.0] - 2026-11-11

- リリース

## [1.0.0] - 2026-11-11

- 正式版リリース（全ツールの動作検証・実機デバッグ完了、Experimental 表記削除）
- Blendshape Mesh Editor 追加（SceneView 上でのメッシュ変形・プロポーショナル編集・BlendShape 作成）
- Git 管理構成の整理（パッケージ本体のみを追跡、不要アセットの除外設定）

## [0.1.8] - 2026-11-11

- readme更新

## [0.1.7] - 2026-11-11

- Blendshape Mesh Editor (Experimental) 追加

## [0.1.6] - 2026-11-09

- Hair Mesh Color Changer / Hair Mesh Deleter 追加

## [0.1.5] - 2026-11-05

- Animation Path Adjuster 追加

## [0.1.4] - 2026-11-05

- Skinned Mesh Updater 追加

## [0.1.3] - 2026-11-05

- Missing Asset Importer の挙動変更（.unitypackage 生成）

## [0.1.2] - 2026-11-03

- Skinned To Static Mesh 追加

## [0.1.1] - 2026-10-31

- Missing Asset Importer 追加

## [0.1.0] - 2026-10-30

- 初回パッケージリリース（VPM / UPM 両対応化）
- **Armature Transfer**: 素体 Armature の Transform・コンポーネント・VRC PhysBone・不足ボーンを衣装側 Armature へコピーする機能
- **New Blendshape From Mix**: 複数の Blendshape を任意の Weight でリアルタイムプレビュー・合成し、非破壊で `.mesh` アセットとして保存する機能
- 既存の `Assets/Cocoa's Toolbox` フォルダからの自動移行（`legacyFolders`）に対応
