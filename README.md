# ClusterPilot Engine

## Senaryo yükleme

`tronloop/{ClusterPilotId}/{VertexId}/scenario/upload` üzerinden şema 2/3 profili alır; 64 kayıt (STOP dahil) ikili listeye derler. Yüklü `Can:Devices` hedefinin ISO-TP soketine 0x12 gönderir, aynı Vertex/UUID/CRC eşleşen 0x04 ACK bekler. Başarı yalnız RAM yükleme onayıdır; test başlatmaz. ACK ve işlem tekrarları `Scenarios:DatabasePath` (geliştirme varsayılanı `data/scenario-uploads.sqlite`) içinde saklanır. Systemd servisinde `Scenarios__DatabasePath=/var/lib/tronloop-clusterpilot-engine/scenario-uploads.sqlite` kullanılır. Böylece servis yazma iznine sahip olur ve kayıtlar deploy sırasında korunur. Mevcut kurulumlarda servis şablonundaki bu ayarı veya aynı değeri içeren bir systemd drop-in dosyasını kurup `systemctl daemon-reload` uygulamak gerekir; yalnız uygulama deploy etmek servis tanımını güncellemez.

Protokol ve sahte taşıyıcı testleri: `dotnet run --project tests/ScenarioTests -- /tmp/scenario-upload-vector.bin /tmp/scenario-upload-64.bin`. Gerçek MQTT/CAN hedeflenmez. Firmware karşılık testi için üretilen paketi `tronloop-vertex-firmware/Tests/scenario_upload_test.c` kullanır. Fiziksel doğrulama için yeni engine dağıtımı ve ayrıca onaylı firmware yüklemesi gerekir.

## Vertex RAM profili

`scenario/profile/query` konusundaki anlık sorgular `RamProfileReader` ile CAN 0x13 komutuna çevrilir. 0x05 yanıtından RAM varlığı, yükleme UUID’si, CRC, kayıt sayısı ve giriş indeksi okunur; `scenario/profile/result` konusuna geri döner. Sorgu salt okunurdur ve en fazla 15 saniye yaşar. Sürüm güncellemesi firmware tarafında da gereklidir; ayrıntılar Defteri Kebir ADR-0033 içinde.
