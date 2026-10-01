# ClusterPilot Engine

## Senaryo yükleme

`tronloop/{ClusterPilotId}/{VertexId}/scenario/upload` üzerinden şema 2/3 profili alır; 32 kayıt (STOP dahil) ikili listeye derler. Yüklü `Can:Devices` hedefinin ISO-TP soketine 0x12 gönderir, aynı Vertex/UUID/CRC eşleşen 0x04 ACK bekler. Başarı yalnız RAM yükleme onayıdır; test başlatmaz. ACK ve işlem tekrarları `Scenarios:DatabasePath` (varsayılan `data/scenario-uploads.sqlite`) içinde saklanır. Bu dizin servis hesabınca yazılabilir olmalı.

Protokol ve sahte taşıyıcı testleri: `dotnet run --project tests/ScenarioTests -- /tmp/scenario-upload-vector.bin`. Gerçek MQTT/CAN hedeflenmez. Firmware karşılık testi için üretilen paketi `tronloop-vertex-firmware/Tests/scenario_upload_test.c` kullanır. Fiziksel doğrulama için yeni engine dağıtımı ve ayrıca onaylı firmware yüklemesi gerekir.
