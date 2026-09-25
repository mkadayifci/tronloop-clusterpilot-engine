# BeagleBone dağıtımı

Uygulama `/opt/tronloop/clusterpilot-engine/current` altında, servis adı
`tronloop-clusterpilot-engine.service`. Kaynak kopyası `source` altında ve origin
`https://github.com/mkadayifci/tronloop-clusterpilot-engine.git` olmalı.

`tronloop-clusterpilot-engine-deploy.timer`, her dakika `main` dalını kontrol eder.
Git HEAD yerine son başarılı dağıtımın `deployed_commit.txt` kaydı karşılaştırılır;
başarısız derleme bir sonraki kontrolde tekrar denenir. Yayın `linux-arm`, self-contained
ve tek dosya olarak hazırlanır. BeagleBone’da .NET 10 SDK, Git ve flock gerekir;
SDK yolu mevcut kurulumda `/home/debian/.dotnet`.

Derleme tamamlanana kadar çalışan sürüme dokunulmaz. Eski sürüm `previous` altında
tutulur. Yeni süreç 15 saniye boyunca ayakta kalamazsa önceki sürüme dönülür;
bu kontrol MQTT veya CAN üzerinden uçtan uca sağlık testi değildir.

SQLite yolu servis ortamında `/var/lib/tronloop-clusterpilot-engine/telemetry.sqlite`
olarak ayarlanır. Bu dizini systemd, `debian` kullanıcısı için oluşturur; dağıtımda
değiştirilmez. Üretime özel ayarlar `/etc/tronloop/clusterpilot-engine/appsettings.Production.json`
içinde tutulabilir. Dosya varsa dağıtım sırasında yeni sürüme kopyalanır; root:debian
sahipliği ve `640` izni kullanılır. Şifre içerebilen bu dosyayı Git’e eklemiyoruz.

Bu dizindeki `.service` ve `.timer` dosyalarının kurulu kopyaları
`/etc/systemd/system/` altında; betik `/opt/tronloop/clusterpilot-engine/` altında.
Bu dağıtım dosyalarını değiştirmek, kurulu kopyalarını kendiliğinden güncellemez.
Kurulu kopyalar güncellendiğinde `sudo systemctl daemon-reload` çalıştırılır.

```sh
sudo systemctl status tronloop-clusterpilot-engine
sudo journalctl -u tronloop-clusterpilot-engine -n 50 --no-pager
sudo systemctl list-timers tronloop-clusterpilot-engine-deploy.timer
sudo journalctl -u tronloop-clusterpilot-engine-deploy -n 50 --no-pager

# Güncelleme kontrolünü elle tetikle:
sudo systemctl start tronloop-clusterpilot-engine-deploy.service
```

Eski Orchestrator kurulumundan geçişin cihaz üstündeki sonuçları ve geri dönüş
adımları Defteri Kebir’deki `docs/03-software/clusterpilot-deployment.md` sayfasında.


Ev dizini kısayolları `show-clusterpilot-engine-logs.sh` ve
`force-deploy-clusterpilot-engine.sh` dosyalarıdır; cihazda `/home/debian/` altında
`debian:debian`, `755` izinleriyle bulunur. İkinci betik dağıtımın `--force` seçeneğini
kullanır: commit aynı olsa bile yeniden derler, mevcut bir dağıtımın kilidini en fazla
30 dakika bekler. Çıktı terminalde görünür. Normal timer değişmeyen commit’i atlar.
Eski Orchestrator kurulumu ve geçiş yedeği kaldırıldı.

Log betiği sudo kullanmaz; `debian` kullanıcısı `systemd-journal` grubundadır.
`/usr/local/bin/show-clusterpilot-engine-logs.sh`, ev dizinindeki betiğe bağlantıdır;
böylece komut her dizinden doğrudan adıyla çalışır.
