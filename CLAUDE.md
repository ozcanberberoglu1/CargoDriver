# CargoDriver — Proje Indeksi

Unity **6000\.3.0f1** (URP 17.3.0) · **Photon PUN 2 v2.54** · Yeni Input System · co\-op kamyon/kargo oyunu.
Aktif branch: `multiplayer`. Diğer branchler: `main`, `lego-fizikleri-yeni`, `sıfırdan`.
Not: `Assets/Photon` ve `Assets/Pack_Pickup` üçüncü parti; `Assets/_Game` proje kodudur.

* * *

## 1\. Akış (sahne zinciri)

```
SampleScene (ana menü)  →  LobbyScene (yükleme/lego)  →  GameScene (sürüş)
   NetworkManager            LobbyController              GameSceneController
   MainMenuController        JoinGameController
                             RadioController
```

| Sahne | Ne yapar | Geçiş |
| --- | --- | --- |
| **SampleScene** | Photon bağlantısı, isim (PlayerPrefs `PlayerName`), oda oluştur/bul (4 haneli `roomId` \+ `password`), 2–N oyuncu | Master `PhotonNetwork.LoadLevel("LobbyScene")` |
| **LobbyScene** | Oyuncular yürür, kargo kutularını lego gibi birleştirir ve kamyona yükler. Kamyon **kinematik/donuk**. Tüm kutular kamyon trigger'ının içine girince "başarı" mesajı → join silindiri açılır | `JoinGameController` → 5 sn geri sayım → `LoadLevel("GameScene")` |
| **GameScene** | Kamyon sürülür, checkpoint/ölüm, kargo kamyon üstünde taşınır | — |

Oda `IsOpen=false` \+ `closed=true` → herkes `LeaveRoom` → `SampleScene`.

* * *

## 2\. Dosya haritası

```
Assets/_Game/Scripts/           ← tüm oyun kodu (5.6k satır)
Assets/_Game/models/characters/toy1/   Toy1.fbx + Idle/Run/Run2/Jumping anim
Assets/_Game/Prefabs/Toys, Art/UI, Fonts, sounds/
Assets/Pack_Pickup/Scripts/     CarControl.cs (araç fiziği), CameraControl.cs (kullanılmıyor)
Assets/Resources/               PUN Instantiate havuzu: Pickup, Toy1, CargoBox, CargoBox2
Assets/Scenes/                  SampleScene, LobbyScene(+lightmap), GameScene, cartoon-bedroom
Assets/_Recovery/0.unity        Unity crash recovery artığı — kullanılmıyor
```

### Script → nerede çalışır

| Script | Satır | Nerede | Rol |
| --- | --- | --- | --- |
| `NetworkManager` | 49 | SampleScene (DontDestroyOnLoad) | Photon connect, `SendRate=30 / SerializationRate=20`, `AutomaticallySyncScene` |
| `MainMenuController` | 320 | SampleScene | Menü/oda listesi/oda kurma |
| `RoomListItem` | 76 | RoomContent prefab | Oda satırı, dolu ise buton kapalı |
| `LobbyController` | 598 | LobbyScene | Spawn, pause paneli, ping, kargo tamam kontrolü, müzik/SFX |
| `JoinGameController` | 617 | LobbyScene | Kontrol tuşu dağıtımı (`ctrl_W/A/S/D/Space`, `ctrl_Behind`), ready, geri sayım, **kargo layout'unu serialize eder** |
| `CargoMachine` | ~230 | LobbyScene | Oyuncu 3D butona tıklar → **master** rastgele lego+renk spawn'lar (event 70, `InstantiateRoomObject`), 50'de durur. Sayaç/hazır bayrağı oda property (`legoCount`/`machineActive`/`legosReady`) |
| `RadioController` | 391 | LobbyScene | Mikrofonla 10 sn kayıt → Photon event ile herkese, echo \+ pitch efekti |
| `GameSceneController` | 472 | GameScene | Kamyon\+kargo spawn, checkpoint, ölüm bölgesi, layer kurulumu |
| `MovingObstacle` (\+Editor) | 106\+259 | GameScene | Kinematik hareketli engel, custom inspector |
| `CarControl` | \~330 | Pickup prefab | Araç fiziği \+ input relay |
| `CarCamera` | 155 | Pickup prefab | Sürücü kamerası (C \= açı değiştir) |
| `ToyController` | 441 | Toy1 prefab | Karakter hareketi, FPS/TPS, pause, araca binme |
| `CargoPickup` | 460 | Toy1 prefab | Kutu tutma, döndürme, scroll mesafe, 2 kemikli kol IK |
| `NetworkedCargoBody` | 808 | CargoBox/CargoBox2 prefab | **Kargo fiziğinin tek otoritesi** |
| `LegoSnap` | 290 | CargoBox/CargoBox2 prefab | Stud geometrisi, snap hedefi bulma |
| `BillboardUI` | 16 | Toy1 prefab | İsim etiketini kameraya çevirir |
| `VehicleInteraction` | 530 | GameScene'de **runtime'da** üretilir (`VehicleInteraction_Local`) | F ile in/bin, exit noktası bulma, haritadan düşme |

* * *

## 3\. Kargo sistemi (projenin kalbi)

`NetworkedCargoBody` tek kural üzerine kurulu: **her cismin her an tam olarak bir writer'ı vardır.** Writer gerçek PhysX çalıştırır, diğer herkes kukla. Rigidbody / isKinematic / useGravity / transform.parent'a bu component dışından dokunulmaz.

**State makinesi** — `Free` (serbest fizik) · `Held` (taşınıyor, velocity servo) · `Stowed` (lego olarak kaynaklanmış; **Rigidbody yok edilir**, carrier'ın compound collider'ına girer) · `Frozen` (oyuncu **Q** ile yerinde sabitledi; **her client'ta kinematik**, dünya-sabit, writer yok, ReferenceFrame'i izlemez — araba/karakter/Held lego çarpınca taş gibi durur, grab reddedilir. `RequestFreeze`/`RequestUnfreeze`, poz reliable state RPC'sinde taşınır).

**İki otorite politikası:**

- `DistributedOwnership` (LobbyScene): kutuyu kapan ownership'i alır, round\-trip yok. Bırakırken ownership kasten thrower'da kalır.
- `HostAuthority` (GameScene): kamyon hareket ettiği için tüm temaslar tek makinede çözülür; grab bir istektir, tutan yerel tahmin yapar (`maxPredictionError` ile sınırlı).

**Moving reference frame:** GameScene'de `NetworkedCargoBody.ReferenceFrame = kamyon`. Pozlar dünya yerine kamyon uzayında akar ve `LateUpdate`'te (kamyon interpolasyonundan sonra) yerleştirilir — kargonun kasada titremesini bu çözer. Ayrıca `PreventSleep=true`, yoksa uyuyan kutu kamyonun altından kaymasını fark etmez.

**Diğer kritik detaylar:** `maxDepenetrationVelocity=1` (kutular kamyonu fırlatmasın), state güvenilir RPC / poz güvensiz stream (eski poz paketleri state eşleşmezse atılır), geç katılan için `OnPlayerEnteredRoom` → 1 sn bekle → state RPC \+ teleport (buffered RPC yok), `pendingCarrierViewId` ile carrier henüz yokken stow ertelenir.

**Snap önizleme (yalnız yerel):** `LegoSnapPreview` (CargoBox/CargoBox2 root'unda) her `TopCollider`'ı yeşil/kırmızı grid'e map'ler. Taşıyan oyuncunun `CargoPickup`'ı her frame `LegoSnap.EvaluatePreview()` çağırıp hedef stud'ların grid'ini `SetActive`'ler: `snapDistance` içinde yeşil, `previewRedDistance`'a kadar kırmızı, ötesi kapalı. Child `SetActive` senkronlanmadığı için diğer oyuncular görmez. Yeşil ⟺ E snap eder.

**Lego akışı:** `LegoSnap.TrySnap()` → `TopCollider*`/`DownCollider*` çift bulur → `AuthorityStow(parent, localPos, localRot)`. Yani snap \= tek bir state geçişi; ayrı bir ağ kanalı yok. Tuşlar: **E** snap, **X** parent'tan ayır, **Z** hepsini ayır (artık **hem LobbyScene hem GameScene**'de aktif — kutu taşınırken çalışır).

**Kargo kaynağı:** Kutular artık sahneye elle yerleştirilmez; **`CargoMachine`** LobbyScene'de runtime'da spawn'lar (master, `InstantiateRoomObject`, rastgele prefab + `_BaseColor` tint'i `instantiationData` ile). Hem "kargo tamam" kontrolü (`LobbyController.CheckAllCargoLoaded`) hem serialize (`SaveCargoPositions`) kutuları **`CargoBox` tag'iyle** dinamik toplar (root'lar seed, welded child'lar `CollectAllLegos` ile parent-first). Tamamlama, makine tüm partiyi bitirene kadar (`legosReady` oda property) bekler — yoksa erken spawn olan birkaç kutu kamyondayken yanlış tamamlanır.

**Lobby → Game aktarımı:** `JoinGameController.SaveCargoPositions()` her kutuyu `localPos,localRot,scale,prefabName,parentIdx,r,g,b,a` olarak `;` ile ayrılmış tek string yapıp oda property `cargoData`'ya yazar. `GameSceneController.SpawnCargoOnPickup()` bunu parse edip `InstantiateRoomObject` ile master'da yeniden kurar (parentler çocuklardan önce yazıldığı için lego ViewID'leri hep hazır).

* * *

## 4\. Araç ve oyuncu

**CarControl:** yalnız **master** simüle eder. Herkes tek tuşuna sahiptir (oda property `ctrl_W` vb. \= actorNumber); non\-master input'unu `RaiseEvent(42, Unreliable, MasterClient)` ile yollar, master hepsini toplayıp clamp'ler. Non\-master kinematik kopyayı `rb.MovePosition` ile takip ettirir — `Physics.autoSyncTransforms` **kapalı** olduğu için transform'a doğrudan yazmak collider'ları bir frame geride bırakırdı.

**Araca binme (F):** iki rol var. *Sürücü* koltuktayken avatarsızdır, inerken `PhotonNetwork.Instantiate` ile avatar doğurur. *Arka yolcu* (`ctrl_Behind`) hep avatarlıdır, koltuğa parent edilmez — her frame `LateUpdate`'te `transform.position = seat.position` (parent etmek PhotonTransformView'a sahte hız gösterip remote kopyayı fırlatıyordu). Binen oyuncunun collider'ı kapanır; yürüyen oyuncu `SetPhysicsGhost` ile tek yönlü katıdır (kendisi durdurulur ama kamyonu itemez).

**Çıkış noktası:** kamyonun etrafında 8 yön taranır, ray \+ `CheckCapsule` ile zemin ve boşluk doğrulanır; hiçbiri olmazsa tavana bırakılır.

**Checkpoint (GameScene, master):** `R` veya deadZone teması → `VehicleInteraction.BoardEveryone()` (event 60, herkes) → kamyon kinematik yapılıp taşınır → kargo snapshot'ı `AuthorityTeleport` ile geri konur → 1 sn sonra fizik açılır \+ `WakeAll()`.

* * *

## 5\. Kontroller

| Tuş | Ne |
| --- | --- |
| WASD / oklar | Yürü · (araçta) sahip olunan tuş |
| Space | Zıpla · (araçta) fren |
| Sol tık | Kutu tut (basılı) |
| Sağ tık (basılı) + sürükle | Legoyu döndür — pozisyon donar; yatay → yaw, dikey → pitch (kamera eksenleri). Snap anında 90°'ye hizalanır |
| Scroll | Tutma mesafesi (1–4 m) |
| E | Lego snap · radyoda kayıt başlat |
| Q | Tutulan legoyu sabitle (Frozen) / tutma tuşu basılı+hedefteyken sabiti çöz |
| X / Z | Parent'tan ayır / hepsini ayır |
| 1 / 2 | Tutulan legoyu büyüt / küçült (0.1 adım, 0.3–2.0 arası). Ölçek üstünde world-space text 2 sn görünüp solar. **Yalnız aynı ölçekli legolar birleşir** |
| C | FPS↔TPS (oyuncu) · kamera açısı (araç) |
| F | Araca bin / in |
| R | Checkpoint respawn (yalnız master) |
| Tab | Oyuncu listesi \+ ping |
| Esc | Pause paneli |

* * *

## 6\. Ağ protokolü özeti

**Oda property'leri:** `roomId`, `password`, `roomName`, `closed`, `ctrl_W/A/S/D/Space`, `ctrl_Behind` (hepsi actorNumber ya da \-1), `ready_<actor>`, `countdown`, `cargoData`, `checkpoint`
**Oyuncu property'leri:** `ping`, `riding`
**Raise event kodları:** `42` araç input · `50–53` radyo (start/data/lock/unlock) · `60` herkesi bindir
**Layer'lar:** `Player`(8), `Vehicle`(9), `pickup`(6) · **Tag:** `CargoBox`
**Fizik:** solver iterations 12, autoSyncTransforms **kapalı**, fixed timestep 0.02

* * *

## 7\. Dikkat edilecekler / bilinen kırılganlıklar

- `LobbyController.SetupCollisionLayers()` Player↔Vehicle çarpışmasını **kapatır**, `GameSceneController` aynı ismli metotla **açar**. Matris global ve sahne yüklemeden sağ çıkar — sıra bozulursa lobide/oyunda çarpışma davranışı ters döner.
- `cargoData` tek bir string'e yazılıyor; kutu sayısı çok artarsa Photon property boyut sınırına dayanabilir.
- `RadioController` ham PCM'i 30 KB'lık reliable chunk'larla yolluyor (8 kHz × 10 sn ≈ 160 KB) — bant genişliği açısından pahalı.
- `CameraControl.cs` eski Input Manager (`Input.GetAxis`) kullanıyor; proje yeni Input System'de. Hiçbir yerde referanslı değil, ölü kod.
- `Assets/_Recovery/0.unity` crash artığı, silinebilir.
- `NetworkedCargoBody` içinde `Policy` / `ReferenceFrame` / `PreventSleep` **static** — sahneyi kuran controller (`Awake`) bunları set etmezse önceki sahnenin ayarı sızar.

## 8\. Geliştirme kuralları (multiplayer\-first)

Bu oyun **multiplayer**. Kullanıcı açıkça belirtti: bundan sonraki her geliştirme, tasarım aşamasından itibaren ağ tarafı düşünülerek yapılacak. Tek oyunculu çalışan bir çözüm önermek yeterli değil — teslimat, "diğer oyuncularda da doğru görünen" çözümdür.

Her yeni sistem/özellik için cevaplanması gereken sorular:

- **Kim otorite?** Bu state'i kim yazar — master mı, sahibi mi, herkes mi? "Her cismin her an tek writer'ı vardır" kuralı bozulmamalı.
- **Diğer oyuncularda nasıl görünür?** Görsel/animasyon/ses yerelse ve replike edilmiyorsa, remote'lar hiçbir şey görmez. Yerel tahmin (prediction) varsa otoriteyle çeliştiğinde ne olacak?
- **Hangi kanal?** State ve tek seferlik olaylar → **reliable RPC / RaiseEvent**. Sürekli poz/değer akışı → **OnPhotonSerializeView (unreliable)**. Kalıcı, geç katılanın da görmesi gereken durum → **oda/oyuncu property**. Unreliable pozların state değişiminden önce yollanmış eski paket olabileceği unutulmamalı (mevcut kodda state karşılaştırılarak eleniyor).
- **Geç katılan (late joiner) ne görür?** Buffered RPC kullanılmıyor; writer `OnPlayerEnteredRoom`'da state'i yeniden bildiriyor. Yeni sistem de aynı yolu izlemeli ya da property üzerinden kendiliğinden ulaşmalı.
- **Ayrılan oyuncu / master değişimi?** `OnPlayerLeftRoom` ve `OnMasterClientSwitched` sonrası state tutarlı kalmalı (tutulan kutu serbest bırakılıyor, kontrol tuşu boşa düşüyor — yeni sistemler de kilit/sahiplik bırakmalı).
- **Sahne geçişini aşıyor mu?** Aşıyorsa `cargoData` gibi oda property'sine serialize edilmeli; static alanlar (`Policy`, `ReferenceFrame`, `PreventSleep`) yeni sahnede mutlaka yeniden set edilmeli.
- **Hareketli referans çerçevesi var mı?** Kamyon üstünde olan her şey dünya uzayında değil kamyon uzayında akmalı, yerleşim `LateUpdate`'te yapılmalı.
- **Bant genişliği.** `SendRate=30 / SerializationRate=20`. Her frame reliable mesaj yollamak, büyük payload'ları tek property'ye sığdırmak veya ham veri broadcast etmek kabul edilebilir değil.
- **Determinizm.** Fizik sonucu iki makinede aynı çıkmaz; sonucu her yerde aynı olması gereken şeyler simülasyondan değil otoriteden gelmeli.

Kod önerirken bu maddeler ayrıca sorulmayı beklemeden dikkate alınır; ihlal riski varsa çözümle birlikte söylenir.
