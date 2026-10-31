# Cocoa's Toolbox

VRChatアバター改変やUnity作業を効率化するエディタ拡張ツール集です。

## 収録ツール

Unity 上部メニューの **`Tools > Cocoa's Toolbox`** から開けます。

- **Armature Transfer** (`Tools > Cocoa's Toolbox > Armature Transfer`)
  - 素体側 Armature の Transform（Position / Rotation / Scale）、汎用コンポーネント、VRC PhysBone（Collider含む）、不足オブジェクトを衣装側 Armature の同名ボーンへ一括コピーします。
- **New Blendshape From Mix** (`Tools > Cocoa's Toolbox > New Blendshape From Mix`)
  - 複数の Blendshape を任意のウェイトでリアルタイムプレビューしながら合成し、元の FBX / Mesh を変更せず非破壊で新しい `.mesh` アセットとしてベイク保存します。
- **Missing Asset Importer** (`Tools > Cocoa's Toolbox > Missing Asset Importer`)
  - Migrate先のプロジェクトにて、元プロジェクトフォルダと Missing になっている Prefab をセットして `GET` ボタンを押すだけで、不足しているアセット（および連鎖する依存ファイル）だけを GUID を維持したまま自動追加します。
