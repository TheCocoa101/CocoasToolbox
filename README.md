# Cocoa's Toolbox

VRChatアバター改変やUnity作業を効率化するエディタ拡張ツール集です。  
**VCC / ALCOM（VPM）** からリポジトリを追加してワンクリックで導入・バージョン更新できます。

---

## VCC / ALCOM への導入方法

1. 以下の公開ページを開き、**「✦ VCC / ALCOM にワンクリックで追加」** ボタンを押します（初回のみ）：
   - **公開ページ**: `https://thecocoa101.github.io/CocoasToolbox/`
   - **VPM リポジトリ URL（手動登録用）**: `https://thecocoa101.github.io/CocoasToolbox/index.json`
2. VCC / ALCOM の各プロジェクト管理画面で **Cocoa's Toolbox** の `+` ボタンを押してインストールします。
   - ※過去に `Assets/Cocoa's Toolbox` フォルダを直接配置していたプロジェクトでも、インストール時に `legacyFolders` 機能によって旧フォルダが自動整理され、スクリプト重複エラーなく移行できます。
   - 新バージョン公開時は、VCC / ALCOM の更新ボタン1つで全プロジェクトをアップデートできます。

---

## 収録ツール

Unity 上部メニューの **`Tools > Cocoa's Toolbox`** から開けます。

- **Armature Transfer** (`Tools > Cocoa's Toolbox > Armature Transfer`)
  - 素体側 Armature の Transform（Position / Rotation / Scale）、汎用コンポーネント、VRC PhysBone（Collider含む）、不足オブジェクトを衣装側 Armature の同名ボーンへ一括コピーします。
- **New Blendshape From Mix** (`Tools > Cocoa's Toolbox > New Blendshape From Mix`)
  - 複数の Blendshape を任意のウェイトでリアルタイムプレビューしながら合成し、元の FBX / Mesh を変更せず非破壊で新しい `.mesh` アセットとしてベイク保存します。
- **Missing Asset Importer** (`Tools > Cocoa's Toolbox > Missing Asset Importer`)
  - Migrate先のプロジェクトにて、元プロジェクトフォルダと Missing になっている Prefab をセットして `GET` ボタンを押すだけで、不足しているアセット（および連鎖する依存ファイル）だけを GUID を維持したまま `.unitypackage` 化してインポート画面を開きます。
- **Skinned To Static Mesh (Experimental)** (`Tools > Cocoa's Toolbox > Skinned To Static Mesh (Experimental)` / Hierarchy 右クリック `Cocoa's Toolbox`)
  - ボーン変形や BlendShape で曲げて形を作った状態の SkinnedMeshRenderer を、見た目上の位置・形状を維持したままボーンなしの StaticMesh（MeshFilter + MeshRenderer / `.mesh`）へ変換します。複製生成・その場変換、指定ボーンへの追従配置、複数メッシュの1メッシュ結合に対応。
- **Skinned Mesh Updater (Experimental)** (`Tools > Cocoa's Toolbox > Skinned Mesh Updater (Experimental)` / Hierarchy・SkinnedMeshRenderer 右クリック)
  - Blender等の外部ツールで編集・再エクスポートしたメッシュを既存の SkinnedMeshRenderer の Mesh 欄に置き換える際、座標系（軸回転）・スケール（×0.01 / ×100 等）・原点オフセット・ボーン順・バインドポーズ軸ズレによる表示破綻を Unity 側のみで自動補正します。シーン上でのリアルタイムプレビュー、新しい `.mesh` アセット作成、GUIDを維持した `.fbx` 上書き保存に対応。

---

## 開発・新バージョンのリリース手順

このリポジトリ自体が **VPM パッケージ開発用の Unity プロジェクト（Unity 2022.3.22f1）** になっています。

1. **開発環境の起動**:
   - VCC / ALCOM の **Add Existing Project** からこのフォルダ（`E:\UnityProjects\CocoasToolbox`）を追加して Unity で開きます。
   - Project ウィンドウ内の `Packages/Cocoa's Toolbox`（実体は `Packages/com.thecocoa101.cocoas-toolbox/`）配下でスクリプトの編集や新規ツールの追加・動作テストを行えます。
2. **新バージョンのリリース**:
   - リポジトリ直下の **`release.bat`** をダブルクリックして実行します。
   - 新しいバージョン番号（Enterキーのみで `1.0.1` のように自動カウントアップ）と変更メモを入力すると、`package.json`・`CHANGELOG.md` の更新から `git commit` ＆ `git push` まで自動で行われます。
   - GitHub にプッシュされると **GitHub Actions** が自動実行され、GitHub Release（`.zip` 作成）と VPM インデックス（`index.json`）が自動更新されます。
