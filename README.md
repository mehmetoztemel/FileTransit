# FileTransit

Bağımsız .NET 10 Windows Forms FTP / FTPS / SFTP istemcisi. Angular ve API projeleriyle kod, proje referansı veya servis bağımlılığı yoktur.

## Başlatma

Windows ve .NET 10 Desktop Runtime gerekir. Geliştirme için .NET 10 SDK:

```powershell
cd <kaynak klasörü>
dotnet run --project FileTransit.csproj -c Release
```

Uygulama: `bin\Release\net10.0-windows\FileTransit.exe`. Solution: `FileTransit.slnx` (uygulama + `Tests\FileTransit.Tests.csproj`). Aynı Windows kullanıcısı için tek uygulama örneği açılır. Başka bilgisayarlara dağıtmak için aşağıdaki "Dağıtım ve kurulum" bölümünü kullanın.

## Dağıtım ve kurulum

İki çıktı üretilir; ikisi de .NET çalışma zamanını içerir, hedef bilgisayarda .NET veya yönetici izni gerekmez (Windows 10/11 x64):

| Dosya | Kullanım |
|---|---|
| `FileTransit-<sürüm>-Kurulum.zip` | ClickOnce kurulumu. Çıkarıp `setup.exe` → Yükle. Kullanıcıya özel kurulur; Başlat menüsü ve masaüstü kısayolu, Ayarlar > Uygulamalar'dan kaldırma. |
| `FileTransit-<sürüm>-win-x64.exe` | Kurulumsuz tek dosya; kopyalayıp çalıştırılır. |

Üretmek için (Visual Studio kurulu olmalı; ClickOnce, Visual Studio'nun MSBuild'iyle yapılır):

```powershell
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

Betik önce sunucusuz testleri çalıştırır, sonra `artifacts\release\<sürüm>\` altına iki dosyayı, son kullanıcı talimatı `KURULUM.txt`'yi ve `SHA256SUMS.txt`'yi koyar. Visual Studio'dan da yapılabilir: proje → Sağ tık → **Yayımla** → `TekExe` veya `Kurulum` profili (`Properties\PublishProfiles\`).

- **Yeni sürüm:** `FileTransit.csproj` içindeki `<Version>` artırılıp betik yeniden çalıştırılır. Kullanıcı yeni paketteki `setup.exe`'yi çalıştırınca kurulum güncellenir; profiller `%LOCALAPPDATA%\FileTransit` içinde olduğu için korunur. Aynı sürüm numarasıyla yeniden dağıtılan paket mevcut kurulumu güncellemez.
- **Otomatik güncelleme yok:** Paket elden (USB, e-posta, bulut) dağıtıldığı için kapalıdır. İleride ağ klasörü veya web adresi kullanılırsa `Kurulum.pubxml` içinde `InstallFrom`/`UpdateEnabled`/`InstallUrl` ayarlanabilir.
- **İmza yok:** Windows "bilgisayarınızı korudu" / "bilinmeyen yayımcı" uyarısı gösterebilir (ayrıntı `build\KURULUM.txt`). Kod imzalama sertifikası alınırsa `Kurulum.pubxml`'de `SignManifests` ve EXE için `signtool` adımı eklenmelidir.

## Sunucular

- **Site yöneticisi**: istediğiniz kadar sunucu ekleyin, kopyalayın, düzenleyin veya silin. Profil adları benzersizdir.
- FTP, explicit FTPS (21), implicit FTPS (990) ve SFTP (22) desteklenir. Protokol değişiminde site yöneticisi varsayılan portu günceller; özel portu elle değiştirebilirsiniz.
- Başlangıç yerel/uzak klasörü, FTP aktif/pasif modu ve bağlantı zaman aşımı profil bazında kaydedilir.
- SFTP için parola veya özel anahtar dosyası kullanılır. Şifreli özel anahtarın parolası parola alanına girilir.
- **Profili kaydet** hızlı bağlantı alanlarını ve mevcut klasörleri kaydeder; var olan adın güncellenmesi onay ister.
- Parola hatırlama isteğe bağlıdır. Hatırlanan parolalar Windows DPAPI / CurrentUser ile şifrelenir; düz metin olarak JSON dosyasına yazılmaz. Başka Windows kullanıcısında/bilgisayarda çözülemeyebilir.
- Her **Bağlan** işlemi ayrı uzak oturum sekmesi açar. **Oturumu kapat** yalnızca o gezinti bağlantısını kapatır; kuyruk işleri kendi bağlantılarıyla devam eder.
- FTPS sertifika doğrulaması devrededir. İlk SFTP bağlantısında SHA-256 sunucu anahtarını doğrulayıp onaylayın. Sonraki bağlantılarda anahtar değişirse bağlantı reddedilir.

## Dosyalar ve klasörler

- Sol panelde yerel dosyalar; sağ panelde seçili uzak oturum bulunur. Ctrl/Shift ile çoklu seçim yapılabilir.
- Klasöre çift tıklayın; adres çubuğuna yol yazıp Enter/Git kullanın. Yerel disk kısayolları ve üst klasör düğmesi bulunur.
- **Yükle** yerel seçimi, **İndir** uzak seçimi kuyruğa ekler. İndirme hedefi sol paneldeki klasördür.
- Klasörler alt klasörleri ve boş klasörleriyle aktarılır. Döngüleri önlemek için sembolik bağlantı/reparse point izlenmez; günlükte belirtilir. Tek ekleme 50.000 öğe / 64 klasör derinliği ile sınırlıdır.
- Windows'a aktarılamayan dosya adları ve ad çakışmaları hata olarak bildirilir; dosya sessizce yeniden adlandırılmaz.
- Yerel dosyaları uzak panele sürüklemek yükler; uzak dosyaları yerel panele sürüklemek indirir. Explorer'dan uzak panele bırakma desteklenir. Uzak dosyaları doğrudan Explorer'a bırakma desteklenmez.
- **Üst klasör** düğmesi, Backspace, Alt+↑ veya farenin geri tuşu bir üst klasöre çıkar ve az önce çıkılan klasörü seçili getirir; kök klasörde düğme pasiftir.
- Üst şeritteki **Ayarlar ▾** düğmesi Site yöneticisi… ve Aktarım ayarları… seçeneklerini alt alta açar. **Profili kaydet** bağlantı satırında, Oturumu kapat düğmesinin yanındadır.
- Sağ tık menüleri, sütun sıralaması, F2 yeniden adlandırma, Delete uzak silme, F5 yenileme bulunur. Silme onay ister; klasör içeriği de silinir.
- Aktarımdan sonra dosya listesini **Yenile/F5** ile güncelleyebilirsiniz.

## Aktarım kuyruğu ve ayarlar

- **Eşzamanlı aktarım = 1**: dosyalar kuyruk sırasıyla aktarılır. **2–4**: bağımsız bağlantılarla paralel aktarılır; farklı sunucu/protokol işleri birlikte çalışabilir.
- **Başlat**, **Duraklat**, **Seçileni iptal et**, **Yeniden dene** ve **Bitenleri temizle** bulunur. Duraklatma aktif aktarımı keser; bekleyen işler korunur. Kuyruk durmuşken seçili iş ↑/↓ ile taşınır.
- **Yeniden dene** seçili başarısız/iptal edilmiş işleri beklemeye alır. Kuyruk durmuşsa ardından Başlat'a basın.
- Ayarlar: otomatik başlatma, 0–10 otomatik yeniden deneme, denemeler arası bekleme, indirme/yükleme hız sınırları, yarım indirmeye devam etme, mevcut dosya politikası.
- Hız sınırı KB/sn ve **aktarım başınadır**; 0 sınırsızdır. Örneğin 4 paralel işte 100 KB/sn ayarı toplamda yaklaşık 400 KB/sn olabilir.
- **Skip** (varsayılan) mevcut hedefi atlar; **Overwrite** üzerine yazar ve yeni işler eklenirken onay ister. Politika iş eklenirken sabitlenir. Diğer ayarlar yeni başlayan işler için geçerlidir.
- İndirmeler hedefin yanında iş kimliğiyle adlandırılmış `.part` dosyasına yazılır, tamamlanınca hedefe taşınır. Devam etme yalnız aynı işin parça dosyası için kullanılır; uzak boyut/tarih değişmişse veya SFTP sunucusu ileri atlamayı desteklemiyorsa baştan indirilir.
- Yarım yükleme sunucuda kalabilir. Aynı iş yeniden denendiğinde kendi yarım yüklemesini baştan yazar. Sunucudaki hedefin yedeği alınmaz.
- Kuyruk, ayarlar, profiller ve güvenilen SFTP anahtarları `%LOCALAPPDATA%\FileTransit\workspace.json` içinde tutulur. Ayrı geliştirilmiş "Portly Desktop"/FileDock sürümü `%LOCALAPPDATA%\PortlyDesktop` kullanır; iki uygulama aynı anda açılabilir ve birbirinin dosyasına yazmaz. İlk açılışta, `FileTransit` klasöründe dosya yoksa, önce bu uygulamanın önceki adıyla oluşan `%LOCALAPPDATA%\Portly` (kuyruk dahil), o yoksa `PortlyDesktop` (kuyruk hariç) içeriği kopyalanır; eski dosyalar değiştirilmez. Kuyruk geri yüklendiğinde işler duraklatılmıştır; Başlat'a basılır. Hatırlanmayan parolalar yeniden istenir. Günlük yalnız oturum belleğindedir.
- Parola hatırlanmayan SFTP anahtarıyla geri yüklenen işte gerekli anahtar parolası Başlat sırasında istenir; şifresiz anahtarda boş bırakılabilir.

## Bu aşamanın sınırları

Bu sürüm FileZilla'nın birebir kopyası değildir. Uzak dosya düzenleme, sunucuda arama ve klasör karşılaştırma sonraki aşamaya bırakılmıştır. FTPS gerçek bir harici sunucuda test edilmedi; FTP ve SFTP testleri geçici yerel sunucularda yapıldı.

## Doğrulama

```powershell
dotnet build FileTransit.slnx -c Release
# Test bağımlılıkları yalnız artifacts klasörüne kurulur:
python -m pip install --target artifacts\test-libs pyftpdlib paramiko
# Bir terminalde yerel test sunucuları:
python Tests\servers.py
# İkinci terminalde:
dotnet run --project Tests\FileTransit.Tests.csproj -c Release -- <kaynak klasörü>\artifacts
# Python yoksa yalnız sunucusuz kontroller (FTP/SFTP entegrasyonunun yerine geçmez):
dotnet run --project Tests\FileTransit.Tests.csproj -c Release -- <kaynak klasörü>\artifacts --ui-only
```

Test sunucuları yalnız 127.0.0.1 adresinde rastgele portlara bağlanır ve yalnız `artifacts/test-server` dizinini sunar. Gerçek sunuculara veya uygulama profillerine dokunulmaz. Test bitince sunucu terminalinde Ctrl+C kullanın.

Kontroller: şifreli profil/kuyruk kaydı, parola hatırlamama, güvenli hedef yolları, FTP/SFTP bağlantı ve listeleme, iptal/devam, dosya içerik karşılaştırması, üzerine yazmadan atlama, klasör oluşturma, yarım yükleme yeniden denemesi, yeniden adlandırma/silme, SFTP anahtar reddi, sıralı/paralel kuyruk, otomatik yeniden deneme, duraklat/devam, profil izolasyonu, karma FTP/SFTP eşzamanlı aktarım, üç pencere boyutunda düğme hizaları, 5.000 öğelik uzak klasör listeleme, 50.000 öğelik sanal dosya listesi ve 50.000 işlik kuyruk (ekleme, sanal tablo, seçim koruma, arka plan kaydı).

