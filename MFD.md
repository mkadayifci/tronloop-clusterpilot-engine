# tronloop-clusterpilot-engine — Proje ve firmware bağlamı

## Güncel telemetri — 2026-09-25

Firmware `tl_payloads.h` ile eşleşen model `Models/VertexTelemetryPayload.cs`.
Paket 13 bayt: byte tür (0x01), ushort mV, short mA, ulong Unix ms.
Çok baytlı alanlar little-endian; `MeasurementTimeMs` ofseti 5. Eski 8 baytlık
`FastTelemetryPayload` modeli kaldırıldı. Sıcaklık ve State telemetride yok.

MQTT topic’i `tronloop/{ClusterPilot:Id}/{VertexId}/vertex-telemetry`.
JSON alanları `PayloadType`, `BatteryVoltageMv`, `BatteryCurrentMa`,
`MeasurementTimeMs`. RTC’den gelen zaman değiştirilmeden yayınlanıyor. Başarısız
yayında SQLite 13 baytı ve yeni tür adını saklıyor. Eski BLOB’lar dönüştürülmüyor;
yeniden gönderici eski/yeni tür ve boyutu ayırmalı.

`MqttMessagePublisher` model türünü açıkça eşliyor: `VertexTelemetryPayload`
`vertex-telemetry`, `VertexStatusPayload` `vertex-status` son ekini kullanıyor.
Diğer türler `NotSupportedException` ile reddediliyor; MQTT yayını veya SQLite
kaydı yapılmıyor. Önceki `base-telemetry` abonelikleri `vertex-telemetry` olarak
güncellenmeli. Tam sabit `Telemetry:MqttTopic` override'ı kendi topic'ini korur;
`{MessageType}` içeren şablonlar yeni adı kullanır. Bu adlandırma firmware'in
0x01 tür baytını, 13 baytlık düzenini veya JSON ölçüm alanlarını değiştirmez.

Her iki Vertex yayını UTF-8 JSON ve QoS 1 kullanır. Vertex status için mevcut
MQTT 5 `MessageExpiryInterval=15` saniye ve gönderilemeyen status'ü SQLite'a
yazmadan atlama davranışı korunur. Telemetriye expiry eklenmez; başarısız
telemetri yayını binary SQLite fallback'e gider.
Kaynak: `MqttMessagePublisher.cs`, `Models/VertexTelemetryPayload.cs`,
`Models/VertexStatusPayload.cs`, `CanPackage.cs`.

Topic değişikliği doğrulaması: Engine derlemesi 0 hata ve 1 NU1900 NuGet erişim
uyarısıyla tamamlandı. Ağsız 10 kontrolde açık tür eşlemesi, JSON/RTC zamanı,
topic override'ı, status expiry/kayıt davranışı, 13 bayt SQLite telemetri kaydı ve
desteklenmeyen tür reddi doğrulandı. Ingestor'ın aboneliği ve topic doğrulaması da
`vertex-telemetry` olarak güncellendi; izole kopyada mevcut 24 kontrol geçti.
Canlı broker/CAN denemesi veya dağıtım yapılmadı.

Bu bölüm telemetri için aşağıdaki eski 7/8/11 bayt değerlendirmelerinin yerine geçer.
Aşağıdaki önceki incelemeler tarihçe olarak korunuyor. 13 bayt modele geçişte,
topic adı değişmeden önce yapılan yerel derleme ve 22 davranış kontrolü geçti:
paket boyut/ofseti, signed akım, uint64 zaman, JSON çıkışı, SQLite
BLOB ve status/heartbeat regresyonları. MQTT istemcisi taklit edildi, SQLite gerçek
geçici dosyada denendi. NuGet güvenlik verisi sorgusunda NU1900 erişim uyarısı vardı.
Canlı broker/CAN denemesi ve canlıya dağıtım yapılmadı.



İlk inceleme: 2026-09-18. Aşağıdaki ilk inceleme notları o tarihteki durumu anlatır;
güncel telemetri modeli ve topic'i için 2026-09-25 bölümleri geçerlidir.

Bu dosya, sonraki geliştirmelerde kullanılacak proje bağlamını, mevcut kodun durumunu ve firmware ile uzlaştırılması gereken protokol ayrıntılarını tutar. Kaynaklar: bu depodaki C# dosyaları ve kullanıcının firmware agentından aktardığı açıklama. Firmware kaynakları veya gerçek CAN trafiği bu incelemede doğrulanmadı. Aşağıdaki öneriler henüz uygulanmış özellikler değildir.

## Amaç

STM32L476 tabanlı Tronloop batarya test cihazından CAN üzerinden gelen veriyi almak, ISO-TP payload'larını ayrıştırmak ve ilgili işleyicilere dispatch etmek. Bilgisayar tarafı, üniversite araştırmasında uzun süreli şarj/deşarj deneyleri, batarya yaşlanma analizi ve bilimsel yayın için zaman damgalı, deney koşullarıyla ilişkilendirilmiş, izlenebilir veri toplamalıdır.

Ham verinin korunması, bağlantı kesintilerinin ve veri boşluklarının görünür olması temel gereksinimlerdir. Bir komutun alınması/gönderilmesi ile cihazda fiziksel olarak uygulanması ayrı durumlardır.

## Firmware bağlamı — kullanıcıdan aktarılan

- MCU: STM32L476.
- BQ25756 şarj/reverse mode kontrolünü, GPIO bus switch güç yolu kontrolünü yürütür.
- INA226 ve BQ34Z100 üzerinden ölçüm alınır.
- `g_tl_context`, ölçümleri ve cihaz durumunu tutar.
- Birimler: mV, mA, ms ve onda bir °C. Pozitif akım şarj, negatif akım deşarjdır.
- Senaryo oynatıcı 32 adımlık bir dizi üzerinden şarj, deşarj, zaman koşulu ve stop işlemlerini yürütür. Diğer koşullar ve bazı eylemler geliştirme aşamasındadır.

### Ölçüm ve durum sınırlamaları

- Batarya akımı ve SOC güncellenir; gerilim/sıcaklık güncellemeleri henüz tamamlanmamıştır.
- SOC mevcut hızlı telemetri paketinde bulunmaz.
- Hızlı telemetride `state` sabit `1` gönderilir; gerçek cihaz durumunun kanıtı olarak kullanılmamalıdır.
- Heartbeat'in kullandığı context durumu ile gerçek senaryo oynatıcı durumu henüz eşitlenmemiştir.
- Sıfır değerler otomatik olarak geçerli ölçüm kabul edilmemelidir. Buna karşılık tüm sıfırları otomatik geçersiz saymak da doğru değildir; ham değer korunmalı, geçerlilik ayrıca belirtilmelidir.

## Haberleşme sözleşmesi — doğrulanacak mevcut biçim

CAN hızı **500 kbps**, bildirilen cihaz ID'si **`0x100`**. Telemetri ve komutlar ISO-TP kullanır; firmware RX/TX tamponları 1024 bayttır. CAN çerçeveleri uygulama payload'ı ayrıştırılmadan önce ISO-TP ile birleştirilmelidir.

Bu depoda Linux `CAN_ISOTP` soketi kullanıldığı için birleştirme kernel tarafında yapılır; `read` ile alınan veri uygulama payload'ıdır. İkinci bir ISO-TP birleştirme katmanı eklenmemelidir.

Yerel ayarlar PC bakış açısından RX=`0x100`, TX=`0x101` kullanır. Firmware açıklamasındaki cihaz ID'si tek başına iki yönün adreslemesini doğrulamaz. Cihaz TX/PC RX, cihaz RX/PC TX ve flow-control adresleri firmware agentıyla netleştirilmelidir. Kod CAN arayüzünün bitrate ayarını yapmaz.

### Cihaz → PC: telemetri

Payload'lar doğrudan packed C yapılarından üretilir ve mevcut STM32 üzerinde çok baytlı alanlar little-endian gönderilir. **Bu mesajlarda 4 baytlık TLP komut başlığı yoktur.**

`packed`, enum alanını tek bayta indirmez. Aşağıdaki ofsetler her iki enum türünün de **4 bayt** olduğu varsayımına dayanır; firmware derleyici ayarları ve `sizeof`/alan ofsetleriyle doğrulanmalıdır. Beklenen boyutlar fast telemetry için **11**, heartbeat için **8 bayt**tır.

#### Fast telemetry — hedef periyot 100 ms

| Ofset | Boyut | Alan | Yorum |
| --- | --- | --- | --- |
| 0 | 4 | `payload_type` enum | `0x01`; beklenen baytlar `01 00 00 00` |
| 4 | 2 | `uint16_t battery_voltage_mv` | mV |
| 6 | 2 | `int16_t battery_current_ma` | mA; işaret korunmalı |
| 8 | 2 | `int16_t battery_temp_decic` | °C = ham değer / 10.0 |
| 10 | 1 | `uint8_t state` | Mevcut firmware'de sabit `1` |

#### Heartbeat — hedef periyot 500 ms

| Ofset | Boyut | Alan | Yorum |
| --- | --- | --- | --- |
| 0 | 4 | `payload_type` enum | `0x02`; beklenen baytlar `02 00 00 00` |
| 4 | 4 | `ScenarioPlayerState_t scenario_player_state` enum | Sayısal enum eşlemesi henüz paylaşılmadı |

100/500 ms değerleri hedef gönderim periyotlarıdır; her paketin tam bu aralıkla geleceği varsayılmamalıdır. Bu biçimde cihaz zaman damgası, ölçüm sıra numarası veya telemetri sürüm alanı bildirilmemiştir.

### PC → cihaz: TLP komutları

Başlık 4 bayttır: `command`, `version=0x01`, `sequence`, `flags` (her biri 1 bayt). Ardından komuta özgü payload gelir.

| Komut | Kod | Başlık sonrası payload |
| --- | --- | --- |
| Ping | `0x70` | Bildirilen ek alan yok |
| Charger enable | `0x21` | Bildirilen ek alan yok |
| Charger disable | `0x22` | Bildirilen ek alan yok |
| Script start | `0x10` | 1 bayt script ID |
| Script stop | `0x11` | Bildirilen ek alan yok |
| Charge limits | `0x23` | Little-endian `uint16` mV + `int16` mA |

Bu komutların çoğu firmware'de henüz yalnızca log üretir. Fiziksel işlem tamamlandı varsayılmamalıdır. `sequence` yönetimi, `flags` anlamları, yanıt/ACK biçimi, timeout ve tekrar deneme davranışı açıklamada tanımlı değildir; uydurulmamalıdır.

## Bu deponun mevcut durumu — koddan incelenen

### Çalışma yapısı

- `Tronloop.ClusterPilot.Engine.csproj`: .NET 10 Worker; `Microsoft.Extensions.Hosting` ve `Microsoft.Extensions.Hosting.Systemd` 10.0.9, MQTTnet 5.2.0.1603 referansları.
- `Program.cs`: generic host oluşturur ve `Worker` servis kaydını yapar. Systemd paketi mevcut olsa da burada özel systemd entegrasyon çağrısı yoktur.
- `CanIsoTpListener.cs`: `libc` P/Invoke üzerinden Linux SocketCAN ISO-TP soketi açar, okur ve yazar. Mevcut taşıma kodu Linux'a yöneliktir.
- `Worker.cs`: CAN dinleyicilerini başlatır ve MQTT bağlantısını yürütür.
- `appsettings.json`: `can0`, RX=`0x100`, TX=`0x101`; virgülle ayrılmış birden fazla RX/TX çifti desteklenir. Çift sayıları eşit değilse CAN dinleyicileri başlatılmaz.

### CAN alımı ve ayrıştırma

- Dinleme döngüsü `Task.Run` içinde çalışır; okuma tamponu 4096 bayttır. Bu boyut firmware'in 1024 baytlık sınırını genişletmez.
- Sokette 1 saniyelik receive timeout ayarlanır; ayarlanamazsa iptal kontrolü yapamayan bir okuma döngüsü başlatmamak için soket açılışı başarısız olur. Bazı okuma hatalarında soket kapatılıp yeniden açılır; belirli hata yollarında 2 saniye beklenir.
- `FastTelemetryPayload`, yalnızca gerilim, akım, sıcaklık ve state alanlarını içerir. `Pack=1` ile **7 bayttır** ve `payload_type` alanı yoktur.
- Parser sadece `bytesRead == Marshal.SizeOf<FastTelemetryPayload>()` koşuluyla çalışır. Aktarılan firmware biçimi 11 baytsa paket parse edilmeden geçilir.
- Yalnız uzunluğa bakılır; mesaj tipi doğrulanmaz. `MemoryMarshal.Read` açık bir little-endian protokol çözümlemesi yapmaz, yerel bellek düzenine dayanır.
- Heartbeat parser'ı, mesaj tipine göre dispatcher, event/channel çıkışı ve CAN telemetrisini MQTT'ye aktaran yol yoktur.
- RX hex metni üretilir ancak onu yazan satırlar yorumdadır. Ham payload kalıcı olarak saklanmaz; tanınmayan uzunluklar için kayıt yoktur.
- `Send` ham byte dizisini yazar; TLP komut oluşturma ve firmware boyut sınırı doğrulaması yoktur.

### Komutlar ve MQTT

- Her açılan CAN dinleyicisi için saniyede bir **12 bayt dummy payload** gönderilir: ilk 4 bayt sayaç, kalan baytlar sıfırdır. Bu TLP komut kodlayıcısı değildir ve gerçek cihaz kullanımından önce kaldırılmalı veya açık bir test seçeneğine bağlanmalıdır.
- Node ID kodda `A0` olarak sabittir.
- MQTT bağlantısı kodda `mqtt.tronloop-lab.com:1883` ve client ID `orchestrator-A0` kullanır. `appsettings.json` içindeki `Mqtt` bölümü mevcut bağlantı kurulurken okunmaz.
- Abonelikler: `tronloop/node/A0/cmd`, `tronloop/broadcast/cmd`.
- Yayınlar: `tronloop/orchestrator/A0/ack`, `/status`, `/heartbeat`. Orchestrator heartbeat'i 5 saniyede bir üretilir; cihazın CAN heartbeat'i değildir ve cihazın hayatta olduğunu kanıtlamaz.
- `start_charge`, `stop_charge`, `set_current`, `ping` MQTT komutları CAN'a gönderilmez. Yerel işleyici başarı ACK'si üretir; “Charge started” gibi yanıtlar fiziksel sonuç kanıtı değildir.
- `NodeCommand` yalnız `Id`, `Type`, `Value` taşır; çoklu cihaz için hedef cihaz alanı yoktur. `set_current` komutunu iki alan gerektiren charge-limits komutuna çevirmek için gerilim sınırının kaynağı/birimi ayrıca tanımlanmalıdır.
- Deney kaydı, kalıcı veri deposu, MQTT kesintisinde veri tamponlama ve MQTT reconnect döngüsü uygulanmamıştır.

### Yaşam döngüsüyle ilgili takip işleri

- İlk `Open()` çağrısı `Worker` içinde başarısız olursa o cihazın dinleme döngüsü başlamaz; döngü içindeki reconnect bu ilk açılış hatasını kapsamaz.
- `ObjectDisposedException` düzeltmesi: CAN görevleri host token'ına bağlı ayrı bir cancellation kaynağı kullanır. MQTT hata yolu dahil `finally` önce bu kaynağı iptal eder, tüm CAN görevlerini bekler ve ardından dinleyicileri dispose eder. Böylece host henüz durdurulmamış olsa bile dummy gönderimi dispose edilmiş dinleyiciyi kullanmaya devam etmez. MQTT reconnect hâlâ uygulanmamıştır.
- Soket açma/kapama kilitli olsa da gerçek `read`/`write` çağrıları kilit dışındadır; eşzamanlı gönderim, reconnect ve dispose davranışı gözden geçirilmelidir.

## Hedef veri akışı ve geliştirme sırası — öneri

Hedef akış: CAN/ISO-TP alımı → ham payload ve alım metadatasının kaydı → tip/uzunluk doğrulaması → açık little-endian ayrıştırma → tipli mesaj dispatch → deney kaydı/MQTT tüketicileri.

1. Firmware ile enum boyutlarını, alan ofsetlerini ve iki yönlü CAN adreslemesini doğrula; her mesaj türü için gerçek örnek hex payload al.
2. Otomatik dummy gönderimini kaldır veya varsayılan kapalı test seçeneğine al. Uygulama ACK'sini cihazda uygulanmış işlem gibi sunmayı düzelt.
3. Taşıma ve parser sorumluluklarını ayır. Doğrulanmış enum genişliğiyle önce türü, sonra türe özgü tam uzunluğu kontrol et. `BinaryPrimitives` gibi açık little-endian okuyucularla signed/unsigned alanları çöz.
4. Fast telemetry ve heartbeat için ayrı tipli mesajlar ve işleyiciler ekle. Kaynak arayüz/RX/TX/cihaz kimliğini dispatch boyunca taşı. Bilinmeyen tür veya bozuk paket dinleme döngüsünü durdurmasın; ham veri ve parse hatası kaydedilsin.
5. Deney oturumu, kalıcı kayıt, kalite bilgisi, kesinti/boşluk takibi ve tüketici yavaşlaması davranışını uygula. MQTT gönderim gecikmesi CAN okuma ve kayıt yolunu kontrolsüzce bloke etmesin.
6. Firmware ile netleşen TLP komut kodlayıcısını ve komut yaşam döngüsünü ekle. Alındı, gönderildi ve cihaz tarafından doğrulandı durumlarını ayır; doğrulama protokolü yoksa fiziksel başarı bildirme.
7. MQTT ayarlarını konfigürasyondan oku; CAN/MQTT reconnect ve kapanış davranışını düzenle.

### Araştırma kaydı için tutulacak bilgiler — öneri

- PC alım anında UTC zaman damgası; aralık/gecikme analizi için monotonik saat değeri. PC alım zamanı cihaz ölçüm zamanı olarak etiketlenmemelidir.
- Deney/oturum kimliği, batarya/numune kimliği, senaryo ve deney koşulları; biliniyorsa firmware sürümü ve parser/protokol biçimi sürümü.
- Kaynak node/cihaz, CAN arayüzü, RX/TX ID'leri, ham ISO-TP payload ve uzunluğu.
- Ham integer ölçümler ve birimleri; parse sonucu, tanınmayan enum değerleri ve alan bazında geçerlilik/güncellik bilgisi.
- Bağlantı değişimleri, yeniden bağlanma, son telemetri/heartbeat zamanı ve beklenen periyoda göre gözlenen veri boşlukları.

Payload'da cihaz zaman damgası ve sıra numarası bulunmadığından kesin ölçüm anı veya kesin kayıp paket sayısı çıkarılamaz. Periyoda göre yapılan boşluk tahminleri tahmin olarak etiketlenmelidir. Ham ISO-TP payload saklamak ile tek tek CAN çerçevelerini saklamak farklıdır; mevcut soket API'si uygulamaya birleştirilmiş payload verir.

## Firmware agentıyla netleştirilecekler

- Gerçek `sizeof(payload_type enum)`, `sizeof(ScenarioPlayerState_t)`, iki payload yapısının boyutları ve alan ofsetleri; enum genişliğini değiştiren derleyici seçenekleri.
- PC RX/TX ve firmware RX/TX CAN ID eşlemesi, standard/extended ID kullanımı ve ISO-TP flow-control ayarları.
- `ScenarioPlayerState_t` sayısal değerleri, `state` anlamı ve context/oynatıcı durum eşitlemesinin tamamlanma durumu.
- Gerilim/sıcaklık ölçümlerinin hazır olma durumu ve alan geçerliliğinin nasıl bildirileceği.
- Gerçekte uygulanmış komutlar, desteklenen script ID'leri, `sequence`/`flags`, cihaz yanıtları ve komut tekrarının etkileri.
- Telemetriye sürüm, cihaz zaman damgası, sıra numarası ve kalite bayrakları eklenip eklenmeyeceği. Bunlar mevcut protokolün parçası sayılmamalıdır.

## Doğrulama kapsamı

İlk bağlam incelemesi kaynak kod ve kullanıcının firmware açıklamasıyla yapıldı. Sonraki `ObjectDisposedException` düzeltmesinde CAN görevlerinin iptal/dispose sıralaması ve receive timeout hata davranışı güncellendi. Donanım testi veya canlı MQTT/CAN bağlantısı çalıştırılmadı.

Parser uygulanırken doğrulanmış örnek payload'lar, negatif akım/sıcaklık, hatalı uzunluk ve bilinmeyen mesaj türü test edilmelidir. Ardından Linux CAN ortamında ISO-TP alımı, gerçek cihazla adresleme, kesinti/reconnect ve kayıt bütünlüğü doğrulanmalıdır.

## SQLite binary kayıt — 2026-09-18 güncellemesi

Bu bölüm 2026-09-18'deki kayıt/parser uygulamasının tarihçesidir. Aşağıdaki 7 baytlık
model, örnek ve testler o sürüme aittir; güncel telemetri kaydı 13 baytlık
`VertexTelemetryPayload` içerir. Status artık SQLite fallback'e yazılmaz.

- Uzunluğa göre çözümleme `CanIsoTpListener.DeserializePayload` metoduna taşındı. Mevcut 7 baytlık `FastTelemetryPayload` biçimi korunuyor; 11 baytlık firmware biçimi henüz uygulanmadı. Desteklenmeyen uzunluklar uyarıyla atlanır.
- Çözümlenen struct, `SqliteTelemetryStore.SaveAsync<T>` içinde `MemoryMarshal.Write` ile binary olarak serialize edilir (`T : unmanaged`). JSON ve ölçüm kolonları yoktur.
- Veritabanı `Telemetry:DatabasePath` ayarından alınır; varsayılan `data/telemetry.sqlite`, uygulamanın content root dizinine göredir. Başlangıçta dosya/tablo oluşturulur. Diğer servis aynı dosyayı açmalıdır.
- `CanTelemetry`: `Id`, `ReceivedAtUtc` (PC alım zamanı, UTC), `CanInterface`, `RxId`, `TxId`, `Payload` (BLOB), `PayloadLength`, `PayloadType` (ör. `FastTelemetryPayload`), `SentAtUtc` (başlangıçta NULL).
- Tek gönderici servis bekleyen kayıtları aşağıdaki sorguyla okur; başarılı gönderimden sonra ilgili `Id` için `SentAtUtc` yazar. Bu proje gönderim yapmaz. Gönderim ile işaretleme arasında çökme olursa aynı kayıt tekrar gönderilebilir; alıcı tarafında kayıt kimliğiyle tekrar kontrolü gerekir. Birden çok gönderici için ayrıca atomik sahiplenme gerekir.

```sql
SELECT Id, PayloadType, Payload, ReceivedAtUtc, CanInterface, RxId, TxId
FROM CanTelemetry
WHERE SentAtUtc IS NULL
ORDER BY Id
LIMIT 100;

UPDATE CanTelemetry SET SentAtUtc = $sentAtUtc
WHERE Id = $id AND SentAtUtc IS NULL;
```

Bilinen tür ve tam uzunluk doğrulandıktan sonra geri okuma:

```csharp
var telemetry = MemoryMarshal.Read<FastTelemetryPayload>(binaryPayload);
```

Okuyucu aynı struct düzenini (`Pack=1`), alan türlerini ve byte sırasını kullanmalıdır. Bu format mevcut unmanaged struct'lar içindir; referans alanlı class'lar için genel amaçlı serializer değildir. Tip düzeni değişirse yeni bir tür adı/sürüm kullanılmalıdır.

SQLite WAL ve FULL synchronous ile açılır. SQL yazma hatasında mevcut kayıt bellekte tutularak 2 saniyede bir tekrar denenir; o dinleyici bu sırada yeni CAN okumaz. Uzun arızalarda kernel tamponu dolabilir; kapanışta henüz yazılmamış kayıt kaybolabilir. Sınırsız/kayıpsız bir ara kuyruk garantisi yoktur.

Doğrulama: `dotnet build --no-restore` hatasız/uyarısız tamamlandı. Geçici SQLite dosyasında binary yazma/geri okuma, negatif akım/sıcaklık, dört eşzamanlı yazma, bekleyen kayıt seçimi ve iptal kontrolü geçti. CAN donanım testi yapılmadı.

## MQTT öncelikli gönderim — 2026-09-25 güncellemesi

2026-09-18'de eklenen MQTT öncelikli gönderim akışını koruyoruz. 2026-09-25'ten
itibaren MQTT payload'ı UTF-8 JSON; CAN verisi ve SQLite fallback kaydı binary kalır.
Kaynak: `MqttMessagePublisher.cs`, `Models/VertexStatusPayload.cs`,
`Models/VertexTelemetryPayload.cs`, `SqliteTelemetryStore.cs`.

- `MqttMessagePublisher.PublishAsync<T>` çözümlenen struct'ı UTF-8 JSON olarak MQTT'ye gönderir. `JsonSerializerOptions.IncludeFields=true` ile public readonly alanlar da yazılır; PascalCase alan adları ve sayısal değerler korunur. QoS 1 kullanılır; broker'ın başarılı publish cevabı alındığında metot döner, SQLite açılmaz/yazılmaz.
- Bağlantı yoksa, publish sonucu başarısızsa, gönderim hata verirse veya 5 saniyelik gönderim süresi aşılırsa `VertexTelemetryPayload` SQLite'a 13 bayt binary olarak kaydedilir. Veritabanı ilk başarısız telemetri gönderiminde oluşturulur. `VertexStatusPayload` güncel durumu taşır: MQTT 5 expiry değeri 15 saniyedir, gönderilemeyen status SQLite'a kaydedilmez; sonraki CAN status'ü beklenir.
- Topic: `tronloop/{ClusterPilot:Id}/{VertexId}/{MessageType}`. Açık tür eşlemesinde `VertexStatusPayload` için son bölüm `vertex-status`, `VertexTelemetryPayload` için `vertex-telemetry` olur. Desteklenmeyen tür `NotSupportedException` ile yayın ve kayıt yapılmadan reddedilir. Kimlik `appsettings.json` içindeki `ClusterPilot:Id` ile belirlenir (varsayılan `clusterpilot-01`); `ClusterPilot__Id=clusterpilot-02` ortam değişkeni bu ayarı ezer. Örnek: `ClusterPilot__Id=clusterpilot-02 dotnet run`. Değişiklik uygulama yeniden başlatıldığında geçerli olur. `Telemetry:MqttTopic` veya `Telemetry__MqttTopic` override'ı verilirse varsayılan topic yerine kullanılır; `{VertexId}` ve `{MessageType}` yer tutucuları desteklenir. Kaynak kimlikleri topic'tedir; telemetri JSON'undaki `MeasurementTimeMs` firmware RTC'sinden gelir, ayrıca PC zaman damgası eklenmez. Worker'ın mevcut `A0` komut/status kimliği ayrıdır.
- Worker ve publisher aynı MQTT istemcisini kullanır. MQTT bağlantı/heartbeat hataları CAN alımını durdurmaz; Worker 5 saniyelik döngüde bağlantıyı yeniden dener.
- Başarı broker onayıdır; tüketicinin işlemi tamamladığının onayı değildir. Broker mesajı alıp onayı kaybolursa SQLite fallback aynı mesajı tekrar göndermeye yol açabilir.
- SQLite'taki eski kayıtlar bu publisher tarafından okunmaz, gönderilmez veya değiştirilmez. Bunları gönderecek ayrı servis için önceki `SentAtUtc` sözleşmesi geçerlidir.


## Vertex adres eşlemesi — 2026-09-24

`Can:Devices`, eski `Can:RxIds` / `Can:TxIds` virgüllü listelerinin yerini alır.
Her kayıt `VertexId`, `RxId`, `TxId` içerir. RX/TX yönleri Pilot tarafına göredir;
Vertex firmware'inde RX ve TX ters eşlenmelidir. Örnek adresler firmware ile aynı olmalıdır.
Aynı `can0` üzerinde vertex-01 için 0x100/0x101, vertex-02 için 0x102/0x103,
vertex-03 için 0x104/0x105 tanımlanmıştır. Kullanılmayan cihaz kayıtları kaldırılabilir.
Kimlikler ve CAN ID'leri cihazlar arasında benzersiz olmalıdır.

Telemetri topic'i 2026-09-25 adlandırmasıyla
`tronloop/{ClusterPilot:Id}/{VertexId}/vertex-telemetry` olarak üretilir.
`Telemetry:MqttTopic` override'ında `{VertexId}` yer tutucusu kullanılabilir;
sabit topic verilirse tüm cihazlar o topic'e yayın yapar.
Ortam değişkeni örnekleri: `ClusterPilot__Id=clusterpilot-02`,
`Can__Devices__0__VertexId=vertex-10`, `Can__Devices__0__RxId=0x110`,
`Can__Devices__0__TxId=0x111`. Ayarlar yeniden başlatmada okunur.
SQLite kayıtlarında mevcut CAN arayüzü/RX/TX alanları korunur; VertexId ayrıca saklanmaz.


## Cihaz aktifliği — 2026-09-24

`Can:Devices` listesinde vertex-01–vertex-16 tanımlıdır; Pilot RX/TX çiftleri
0x100/0x101 ile başlayıp 0x11E/0x11F ile biter. İlk üç cihaz aktif, diğerleri pasiftir.
Her cihazın `IsInstalled` alanı listener açılıp açılmayacağını belirler. `false` olan
cihaz için socket veya dinleme görevi oluşturulmaz.
Alan belirtilmezse eski konfigürasyonlarla uyum için `true` kabul edilir.
Bu alan cihazın çevrimiçi durumunu değil, yapılandırmadaki etkinliğini gösterir.
Örnek ortam değişkeni: `Can__Devices__3__IsInstalled=true` vertex-04 cihazını etkinleştirir.
Değişiklik uygulama yeniden başlatıldığında geçerli olur.

## Listener durum takibi — 2026-09-24

Yalnızca `IsInstalled=true` cihazlar için listener başlatılır.
Tüm cihazlar `tronloop/orchestrator/A0/status` ve `tronloop/{ClusterPilot:Id}/heartbeat` JSON
mesajlarının `Devices` dizisinde raporlanır. Heartbeat mevcut 5 saniyelik döngüde,
MQTT bağlantısı varken gönderilir. Her kayıt `VertexId`, `IsInstalled`,
`ListenerState`, `ReceptionState`, `LastReceivedAtUtc`, `ReceivedPackets`,
`LastError` içerir. Snapshot okumaları ve sayaç güncellemeleri kilitle korunur.

- ListenerState: `inactive`, `starting`, `listening`, `reconnecting`, `faulted`, `stopped`.
- ReceptionState: `inactive`, hiç paket alınmadığında `waiting`, yakın zamanda paket
  alındığında `recent`, son paket `Can:StaleAfterSeconds` (varsayılan 30) kadar
  eskidiğinde `stale`. Bu eşik pozitif olmalıdır; cihazın yayın sıklığına göre ayarlanır.
- `listening` socket'in açıldığını gösterir; cihazın çevrimiçi olduğunun kanıtı değildir.
- Sayaç ve son alım zamanı, bilinmeyen payload boyutları dahil tüm alınan ISO-TP
  paketlerini kapsar. `LastError` son listener hatasının geçmiş bilgisidir;
  bağlantı düzelince korunur. Bu alanlar firmware'in batarya/state alanından ayrıdır.
- Başlangıçta socket açılamazsa da listener görevinde yeniden deneme yapılır.
  Kapanışta socket kapatılır ve durum `stopped` olur.
- Config aktifliği başlangıçta okunur; `IsInstalled` değişikliğinde yeniden başlatma gerekir.

Doğrulama: build ve donanımdan bağımsız durum/geçiş kontrolleri;
gerçek Linux CAN/ISO-TP bağlantısı ve MQTT tüketicisi ile entegrasyon testi yapılmadı.


## Dummy gönderimin kaldırılması — 2026-09-24

Otomatik 12 baytlık dummy gönderim görevi ve `SendDummyCanMessagesAsync` metodu
kaldırıldı. Önceki bölümlerdeki dummy gönderim açıklamaları tarihsel durumu anlatır.
CAN listener alımı ve yeniden bağlantı denemeleri devam eder; uygulama periyodik
test payload'ı göndermez. ISO-TP flow-control için TX ID kullanılmaya devam eder.


## VertexStatusPayload — 11 bayt firmware biçimi

Bu bölüm ilk uygulamayı anlatır. Uzunluğa göre tür seçimi ve 7 baytlık eski
FastTelemetry desteği aşağıdaki payload type güncellemesiyle kaldırıldı;
buradaki binary MQTT yayını da 2026-09-25'te JSON ile değiştirildi. Aşağıdaki
status SQLite fallback anlatımı da tarihçedir; güncel status gönderilemezse atlanır.

11 baytlık mesajlar artık `VertexStatusPayload` olarak çözümlenir; eski 7 baytlık
`FastTelemetryPayload` desteği de korunur. Alan sırası: byte PayloadType,
ushort BatteryVoltageMv, byte ScenarioPlayerState, byte ChargerMode,
short BatteryCurrentMa, short BatteryTemperatureDc, short AmbientTemperatureDc.
Çok baytlı alanlar açıkça little-endian okunur; struct Pack=1 ile 11 bayttır.
Akımda artı şarj, eksi deşarj; sıcaklıklar derece C'nin onda biri birimindedir.
Voltaj context değeridir, ölçüm güncellemesi henüz firmware'de uygulanmamıştır;
charger mode donanım geri bildirimi değildir.

Tür seçimi uzunluğa göredir. PAYLOAD_TYPE_GENERAL_STATUS ve enum sayısal değerleri
verilmediği için PayloadType, ScenarioPlayerState ve ChargerMode ham byte olarak
korunur; payload type doğrulaması yapılmaz. MQTT mevcut base-telemetry topic'ine
binary yayınlar; SQLite fallback PayloadType kolonuna `VertexStatusPayload` yazar.
Mevcut MemoryMarshal tabanlı binary yayın/kayıt, little-endian host varsayar.


## Payload type tabanlı seçim

Bu bölüm 8 baytlık ara telemetri şemasının tarihçesidir. Güncel 0x01 paketi,
üstte açıklanan 13 baytlık `VertexTelemetryPayload` modelidir; eski 7/8 baytlık
paketler desteklenmez. Tür baytıyla seçim ve uzunluk doğrulaması devam eder.

Tür artık ilk bayttan seçilir: 0x01 FastTelemetry, 0x02 Heartbeat, 0x03 VertexStatus.
Uzunluk yalnızca seçilen türün şemasını doğrular; boyuttan türe geri dönüş yoktur.
FastTelemetry mevcut alanlarının önüne tür baytı eklenerek 8 bayt oldu;
eski tür baytı olmayan 7 baytlık paketler desteklenmez. VertexStatus 11 bayttır.
Her iki parser little-endian okur ve tür/boyut doğrular. CAN ve SQLite binary
verisi tür baytını da içerir; MQTT JSON'unda aynı değer sayısal `PayloadType`
alanıdır. Önceden kaydedilmiş 7 baytlık FastTelemetry kayıtları
bu değişiklikle dönüştürülmez; okuyucular PayloadLength ile eski biçimi ayırmalıdır.
Heartbeat kodu tanınır ancak gövde şeması henüz verilmediği için loglanıp atlanır.
Bilinmeyen türler ve hatalı boyutlar da loglanıp atlanır; socket yeniden açılmaz.

## ClusterPilot MQTT heartbeat — 2026-09-25

ClusterPilot heartbeat'i `tronloop/{ClusterPilot:Id}/heartbeat` topic'ine JSON olarak
gönderiyoruz. Mevcut ayarla topic `tronloop/clusterpilot-01/heartbeat` olur;
`ClusterPilot__Id` ortam değişkeni hem topic'teki hem payload'daki kimliği değiştirir.
Kimlik boş olamaz ve `/`, `+`, `#` veya NUL içeremez; başlangıçta doğrulanır.
Kaynak: `Worker.cs`, `Models/ClusterPilotHeartbeat.cs`, `appsettings.json`.

İlk MQTT bağlantısında hemen, ardından bağlantı varken 5 saniyelik timer ile
yayımlanır. Bağlantı veya gönderim hatasında mevcut döngü yeniden dener;
ağ işlemi uzarsa teslim aralığı da uzayabilir. Heartbeat'ler SQLite'a yazılmaz,
birikmiş heartbeat'ler sonradan gönderilmez. QoS 0 kullanılır ve retained değildir.
MQTT client ID'si `engine-{ClusterPilot:Id}` olduğundan farklı ClusterPilot
kimlikleri aynı broker'a ayrı istemciler olarak bağlanır.

JSON alanları `ClusterPilotId`, `State` (`alive`), `Devices` ve `TimestampUtc`'dir.
`TimestampUtc` yayın hazırlanırken alınan UTC zamanıdır; `Devices` mevcut listener
durumlarını içerir. Bu mesaj Engine'in çalıştığını bildirir, Vertex'lerin çevrimiçi
olduğunu tek başına kanıtlamaz. MQTT istemcisinde tüm ClusterPilot heartbeat'lerini
izlemek için `tronloop/+/heartbeat` konusuna abone olunabilir.

`tronloop/clusterpilot-01/heartbeat` için örnek payload:

```json
{
  "ClusterPilotId": "clusterpilot-01",
  "State": "alive",
  "Devices": [
    {
      "VertexId": "vertex-01",
      "IsInstalled": true,
      "ListenerState": "listening",
      "ReceptionState": "recent",
      "LastReceivedAtUtc": "2026-09-25T09:00:04+00:00",
      "ReceivedPackets": 128,
      "LastError": null
    }
  ],
  "TimestampUtc": "2026-09-25T09:00:05+00:00"
}
```

Örnekte yalnızca bir cihaz var. Gerçek yayında `Can:Devices` içindeki tüm cihazlar,
`IsInstalled=false` olanlar dahil, `Devices` dizisinde yer alır. Henüz paket
alınmamış cihazın `LastReceivedAtUtc` alanı `null` olur. Bu JSON'un mevcut modeli
`Models/ClusterPilotHeartbeat.cs`; cihaz alanlarının kaynağı `CanDeviceStatus.cs`.

Önceki `tronloop/orchestrator/A0/heartbeat` yayını bu topic'e taşındı;
heartbeat tüketicileri topic'i ve `NodeId` yerine `ClusterPilotId` alanını kullanmalı.
Komut, ack ve status topic'leri mevcut `A0` yapısında devam eder.

Doğrulama: `dotnet build --no-restore` hatasız ve uyarısız geçti. Ağsız sahte MQTT
istemcisiyle heartbeat'ler yaklaşık 0,05 / 5,03 / 10,03 saniyede gözlendi; topic,
JSON alanları, istemci kimliği, geçersiz kimliklerin reddi ve temiz kapanış kontrol
edildi. Gerçek broker/CAN bağlantısıyla entegrasyon testi yapılmadı.

## Vertex MQTT topic'leri ve JSON payload — 2026-09-25

`VertexStatusPayload` artık varsayılan olarak
`tronloop/{ClusterPilot:Id}/{VertexId}/vertex-status` topic'ine gönderilir.
`VertexTelemetryPayload` için topic
`tronloop/{ClusterPilot:Id}/{VertexId}/vertex-telemetry` olur. Desteklenmeyen
model türü varsayılan bir telemetri topic'ine düşmez; yayın ve kayıt yapılmadan
`NotSupportedException` ile reddedilir.
`Telemetry:MqttTopic` override'ı `{VertexId}` yanında `{MessageType}` yer tutucusunu
kullanabilir. Tam sabit topic override'ı verilirse yine önceliklidir.

`tronloop/clusterpilot-01/vertex-01/vertex-status` için örnek payload:

```json
{
  "PayloadType": 3,
  "BatteryVoltageMv": 3700,
  "ScenarioPlayerState": 1,
  "ChargerMode": 0,
  "BatteryCurrentMa": -250,
  "BatteryTemperatureDc": 245,
  "AmbientTemperatureDc": 230
}
```

Değerler gösterim içindir; gerçek ölçüm değildir. Akım mA, voltaj mV, sıcaklıklar
derece C'nin onda biri olarak kalır: örneğin `245`, 24,5 °C demektir. Akımın işareti
korunur; pozitif şarj, negatif deşarjdır. `ScenarioPlayerState` ve `ChargerMode`
ham sayısal kodlardır; bu örnek kodlara bir durum adı atamaz. Voltaj ve charger mode
context alanlarıdır; tek başına ölçümün veya donanım geri bildiriminin kanıtı değildir.

`tronloop/clusterpilot-01/vertex-01/vertex-telemetry` için örnek payload:

```json
{
  "PayloadType": 1,
  "BatteryVoltageMv": 3700,
  "BatteryCurrentMa": -250,
  "MeasurementTimeMs": 1790326800000
}
```

`VertexTelemetryPayload` alanları `PayloadType` (`1`), `BatteryVoltageMv`,
`BatteryCurrentMa` ve `MeasurementTimeMs`'dir. Son alan Vertex RTC'sinden gelen
Unix milisaniyesidir; Engine bunu PC alım zamanı ile değiştirmez. Telemetride
sıcaklık ve `State` alanı yoktur. Her iki Vertex payload'ında public readonly alanlar
`IncludeFields=true` ile seri hale gelir; `WireSize` sabiti JSON'a yazılmaz.
Kaynak: `MqttMessagePublisher.cs`, `Models/VertexStatusPayload.cs`,
`Models/VertexTelemetryPayload.cs`.

Önceki `base-telemetry` topic'ini dinleyen tüketici artık `vertex-telemetry`ye
abone olmalıdır. JSON, QoS 1 ve mevcut 13 baytlık CAN telemetri düzeni korunur.
Telemetri için expiry yoktur ve gönderim başarısızlığında binary SQLite fallback
çalışır. Vertex status'ün 15 saniyelik MQTT 5 expiry değeri ve gönderilemeyen
status'ü SQLite'a yazmadan atlama davranışı devam eder.

İlk JSON geçişinin tarihsel doğrulaması: `dotnet build --no-restore` hatasız ve
uyarısız geçti. Bu kontrol eski 8 bayt telemetri ve status'ün SQLite'a yazıldığı
önceki davranışa aittir; güncel 13 bayt telemetri/topic değişikliğinin testi değildir.
Ağsız MQTT
istemcisiyle iki modelin UTF-8 JSON alanları, negatif sayıları, topic override'ları,
QoS 1 ve başarılı yayında veritabanı oluşturulmaması kontrol edildi. Bağlantısız,
reddedilen ve hata veren gönderimlerde SQLite BLOB'larının orijinal 11/8 baytı
koruduğu doğrulandı; heartbeat JSON alanları da kontrol edildi. Canlı broker ve
CAN donanımıyla bu JSON değişikliğinin entegrasyon testi yapılmadı.
