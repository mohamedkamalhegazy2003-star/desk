# DuoSeat

> **v0.5:** دعم Windows 10 و 11 عبر **وضع الجهاز الافتراضي** (`run --vm`): المحطة 2 = VM على Hyper-V تشارك موارد الجهاز (CPU/RAM) وتعمل على شاشة منفصلة باستخدام منفصل.

> **v0.4:** أُضيف **الوكيل (Agent)** الذي يعمل داخل جلسة المحطة 2 ويعطيها مؤشر ماوس ولوحة مفاتيح مستقلَّين (انظر قسم الوكيل أدناه).

محطتان (مستخدمان) على كمبيوتر واحد، لكل محطة شاشة ولوحة مفاتيح وماوس. *Two stations on one Windows PC.*

> ⚠️ **تجريبي (v0.3).** كود الدرايفر والخدمة **لم يُترجم ولم يُختبر على أجهزة حقيقية** من قِبل كاتبه. جرّب داخل **جهاز افتراضي (VM) أو جهاز تجارب** فقط، ولا تثبّت الدرايفر على جهازك الأساسي قبل أن تتأكد أنك تعرف طريق التراجع (انظر "الاسترجاع").

## المكوّنات

| المجلد | الوصف |
|---|---|
| `src/DuoSeat/` | التطبيق الرسومي (v0.2): توزيع في وضع المستخدم فقط (Raw Input + Hooks)، بلا درايفر. |
| `driver/DuoSeatFilter/` | **درايفر KMDF (C++)**: فلتر علوي لفئتي الكيبورد والماوس. يعرف أي جهاز فيزيائي أنتج كل ضغطة، ويمرّرها لويندوز أو يحجبها ويرسلها للخدمة. |
| `service/DuoSeat.Service/` | **الخدمة/الأداة (C#, .NET 8)**: تتصل بالدرايفر، تعيّن الأجهزة للمحطات، تستقبل مدخلات المحطة 2 وتوجّهها لنافذة RDP على الشاشة الثانية. |
| `agent/DuoSeat.Agent/` | **الوكيل (C#)**: يعمل داخل جلسة المحطة 2 (كمستخدمها)، يستقبل الأحداث من الخدمة عبر Named Pipe ويحقنها محلياً في تلك الجلسة، ويرسم مؤشرها الخاص. |
| `scripts/create-station2-vm.ps1` | ينشئ VM على Hyper-V للمحطة 2 (CPU/RAM/قرص، وTPM لـ Windows 11). |
| `include/duoseat_shared.h` | العقد المشترك (IOCTL + هياكل البيانات) بين الدرايفر والخدمة. |
| `scripts/` | سكربتات PowerShell: توقيع تجريبي، تثبيت الخدمة، تفعيل/تعطيل الفلتر. |

## كيف يعمل

```
لوحة مفاتيح/ماوس المحطة 1 ──► kbdclass/mouclass ──► ويندوز (سطح المكتب)   [بلا أي تدخل]
لوحة مفاتيح/ماوس المحطة 2 ──► DuoSeatFilter (يحجبها) ──► DuoSeat.Service ──► نافذة RDP على الشاشة 2
```

1. الدرايفر مفلتر علوي (UpperFilters) **تحت** `kbdclass`/`mouclass`، فيستبدل دالة الاستدعاء (callback) كما في عيّنات WDK `kbfiltr`/`moufiltr`.
2. كل جهاز له محطة: `1` تمرير عادي، `2` حجب وإرسال للخدمة، `3` مراقبة (نسخ + تمرير، لتعرّف الأجهزة).
3. الخدمة تقرأ الأحداث عبر IOCTL معلّق (inverted call) من `\\.\DuoSeatCtl` وتحقنها بـ `SendInput` في نافذة RDP.
4. **أمان:** الحجب يعمل فقط والخدمة فاتحة مقبض التحكم. إغلاق الخدمة أو انهيارها يعيد كل الأجهزة لويندوز تلقائياً.

### حدود مهمة (صريحة)

- **الجلسات المتزامنة:** ويندوز 10/11 يسمح بجلسة تفاعلية واحدة. لمحطتين: إما VM (قسم Windows 10/11 أعلاه) أو Windows Server + Remote Desktop Services. المشروع لا يتجاوز الترخيص.
- في الوضع `--legacy` يوجد مؤشر واحد مشترك يقفز بين الشاشتين. الوضع الافتراضي (v0.4) يستخدم الوكيل لمؤشر مستقل.
- التركيز (focus) يعود لنافذة المحطة 1 بعد خمول المحطة 2 (`FocusLeaseMs`، الافتراضي 400ms). الكتابة المتزامنة من المحطتين غير سلسة.
- لا يعمل مع اللمس/القلم، ولا يوزّع الصوت/الطابعات/USB. `Ctrl+Alt+Del` و Pause لا تُمرَّر.
- الدرايفر يُنصَّب كفلتر على **فئة** الكيبورد والماوس كاملة (يمرّر كل شيء ما عدا أجهزة المحطة 2).

## Windows 10/11: المحطة 2 كجهاز افتراضي (VM)

ويندوز 10/11 (نسخ العميل) يسمح بجلسة تفاعلية **واحدة**، ولا يوجد وضع مرخّص لجلستين متزامنتين فيه (هذا لـ Windows Server فقط). المشروع **لا** يعدّل `termsrv.dll` ولا يستخدم RDP Wrapper لأن ذلك تجاوز لقيد الترخيص. الحل المتوافق مع Win10/11:

- **المحطة 1:** ويندوزك الحالي على الشاشة 1.
- **المحطة 2:** جهاز افتراضي (Windows 10/11 كضيف، **برخصته الخاصة**) على **Hyper-V** (يتطلب Pro/Enterprise/Education؛ Home بلا Hyper-V) يظهر ملء الشاشة على الشاشة 2.
- **الموارد المشتركة:** المعالج والذاكرة والقرص من نفس الجهاز، تحددها أنت للـ VM (`-Cpu` و `-MemoryGB`).
- الوكيل يعمل **داخل الـ VM** ويتصل بالخدمة عبر TCP على محوّل Hyper-V الافتراضي فقط (برمز سري مشترك Token)، فيكون المؤشر والتركيز مستقلَّين تماماً.

الخطوات:
1. `.\scripts\create-station2-vm.ps1 -IsoPath D:\Win11.iso -Win11` ثم ثبّت ويندوز داخل الـ VM.
2. `vmconnect.exe localhost DuoSeat2` وضعها ملء الشاشة على الشاشة 2.
3. على المضيف: `DuoSeat.Service agent-cmdline` ← انسخ `DuoSeat.Agent.exe` إلى الـ VM ونفّذ الأمر المطبوع هناك (وأمر `reg add` لتشغيله تلقائياً).
4. وزّع الأجهزة (`identify` / `assign`) ثم `DuoSeat.Service run --vm`.

حدود وضع الـ VM: بطاقة الرسوميات في الـ VM افتراضية (الألعاب/الأعمال الثقيلة ضعيفة)، والصوت و USB وبقية الأجهزة غير موزّعة. الرمز السري يُرسل نصاً عبر المحوّل الافتراضي المحلي للمضيف، فلا تغيّر `VmListenAddress` إلى عنوان شبكتك. يمكن نظرياً استخدام برامج VM أخرى (VirtualBox/VMware) لأن النقل TCP عام، لكن لم يُجرَّب.

## الوكيل: ماوس مستقل على الشاشة الثانية

```
ماوس المحطة 2 ─► DuoSeatFilter ─► DuoSeat.Service ──(Named Pipe)──► DuoSeat.Agent  [داخل جلسة المحطة 2]
                                                                     ├─ SendInput داخل الجلسة (مؤشر وتركيز مستقلان)
                                                                     └─ نافذة مؤشر شفافة ترسم شكل المؤشر الحالي
```
- لأن الحقن يتم **داخل جلسة المحطة 2** لا في جلسة المحطة 1، لا يتحرك مؤشر المحطة 1 ولا يتنازع التركيز معه.
- عميل RDP (mstsc) يرسم المؤشر عند مكان **الماوس المحلي** فقط، لذلك تحصر الخدمة ماوس المحطة 1 داخل الشاشة 1 (`ClipCursor`) ليبقى بعيداً عن نافذة RDP، ويرسم الوكيل مؤشر المحطة 2 بنفسه فوق الصورة (يأخذ الشكل من `GetCursorInfo`: سهم، I-beam، تحجيم...).
- الخدمة تقبل الاتصال من مستخدم المحطة 2 فقط (تتحقق من اسمه عبر الـ pipe).

الإعداد:
1. اجعل مستخدم المحطة 2 وجلسة RDP كما في v0.2، ثم: `DuoSeat.Service set user DuoSeat2` (إن كان الاسم غير الافتراضي).
2. ضع `DuoSeat.Agent.exe` بجانب `DuoSeat.Service.exe` ونفّذ: `DuoSeat.Service install-agent` (يسجّله في Run ويعمل لمستخدم المحطة 2 فقط).
3. سجّل دخول جلسة المحطة 2 عبر RDP على الشاشة 2، ثم شغّل `DuoSeat.Service run`. سيطبع "Agent connected." عند اتصاله.
4. للعودة للوضع القديم (مؤشر مشترك): `DuoSeat.Service run --legacy`.

حدود الوكيل: لا يغطي شاشة قفل ويندوز/UAC (سطح مكتب آمن)، ولا يعمل لو لم تكن جلسة المحطة 2 نشطة. أثناء `run` ماوس المحطة 1 محصور في شاشته. إن لم يتصل الوكيل تُهمَل مدخلات المحطة 2. شكل المؤشر المرسوم تقريب (بدون شفافية ألفا كاملة). وهذا الجزء **لم يُجرَّب** — خصوصاً سلوك عميل RDP مع المؤشر.

## هيكل المشروع

```
DuoSeat/
├── DuoSeat.sln
├── README.md  LICENSE  .gitignore
├── .github/workflows/build.yml        # يبني التطبيق والخدمة (C#) فقط
├── include/
│   └── duoseat_shared.h
├── driver/DuoSeatFilter/
│   ├── DuoSeatFilter.vcxproj
│   ├── DuoSeatFilter.inf              # يسجّل الخدمة + ربط KMDF 1.15
│   ├── driver.h
│   └── driver.cpp
├── agent/DuoSeat.Agent/
│   ├── DuoSeat.Agent.csproj
│   └── Program.cs  Injector.cs  CursorOverlay.cs
├── service/DuoSeat.Service/
│   ├── DuoSeat.Service.csproj
│   ├── Program.cs  DriverClient.cs  Router.cs  ServiceConfig.cs  AgentPipe.cs
├── scripts/
│   ├── sign-test.ps1  install-driver.ps1  enable-filter.ps1  disable-filter.ps1
└── src/DuoSeat/                        # تطبيق v0.2 كما هو
```

## البناء (Build)

### المتطلبات
- Windows 10 (1607+) / 11 x64، **Visual Studio 2022** مع workload *Desktop development with C++* و *.NET desktop development*.
- **Windows Driver Kit (WDK)** المطابق لنسخة Windows SDK المثبتة (إضافة VS + حزمة WDK).
- .NET 8 SDK.

### الخدمة والتطبيق (C#)
```
dotnet publish service/DuoSeat.Service/DuoSeat.Service.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-service
dotnet publish agent/DuoSeat.Agent/DuoSeat.Agent.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-agent
dotnet publish src/DuoSeat/DuoSeat.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
أو ارفع المجلد على GitHub وسيبنيهما GitHub Actions (تبويب Actions ← Artifacts).

### الدرايفر (C++)
افتح `DuoSeat.sln` في VS ← اختر `Debug | x64` ← Build على مشروع `DuoSeatFilter`، أو من *Developer Command Prompt*:
```
msbuild driver\DuoSeatFilter\DuoSeatFilter.vcxproj /p:Configuration=Debug /p:Platform=x64
```
الناتج: `driver\DuoSeatFilter\x64\Debug\DuoSeatFilter.sys`.
إن لم يكن WDK مثبتاً سيفشل هذا المشروع (ويبقى باقي الحل يُبنى بـ `dotnet`). لا يبني GitHub Actions الدرايفر لأن WDK غير مثبّت على المشغّلات افتراضياً.

## التثبيت للتجربة (جهاز تجارب / VM — بصلاحيات Administrator)

1. فعّل وضع التوقيع التجريبي وأعد التشغيل (يتطلب إيقاف Secure Boot):
   `bcdedit /set testsigning on`
2. وقّع الدرايفر (من Developer PowerShell):
   `.\scripts\sign-test.ps1 -SysPath .\driver\DuoSeatFilter\x64\Debug\DuoSeatFilter.sys`
3. سجّل الخدمة: `.\scripts\install-driver.ps1 -SysPath .\driver\DuoSeatFilter\x64\Debug\DuoSeatFilter.sys`
4. فعّل الفلتر على الفئتين ثم **أعد التشغيل**: `.\scripts\enable-filter.ps1`

## الاستخدام

```
DuoSeat.Service list                 # الأجهزة التي يراها الدرايفر
DuoSeat.Service identify             # حرّك كل ماوس/اضغط على كل كيبورد لتعرف رقمه (Ctrl+C للخروج)
DuoSeat.Service assign 7 2           # ضع الجهاز رقم 7 على المحطة 2 (يُحفظ بالـ Instance ID)
DuoSeat.Service monitors             # فهارس الشاشات
DuoSeat.Service set monitor2 1       # شاشة المحطة 2
DuoSeat.Service run                  # ابدأ التوجيه (Ctrl+C أو Ctrl+Alt+F12 للإيقاف)
```
جهّز مستخدم المحطة 2 وجلسة RDP الـ loopback كما في تطبيق v0.2 (تبويب General Settings)، ثم شغّل `run`. الإعدادات في `%ProgramData%\DuoSeat\service.json`.

## الاسترجاع (إن تعطّل الكيبورد/الماوس)

الفلتر مسجّل في `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4D36E96B-E325-11CE-BFC1-08002BE10318}` (كيبورد) و `...{4D36E96F-E325-11CE-BFC1-08002BE10318}` (ماوس) ضمن قيمة `UpperFilters`. إن فشل تحميل الدرايفر (غير موقّع/بناء خاطئ) قد لا تعمل الأجهزة. ادخل Safe Mode أو WinRE ثم شغّل `.\scripts\disable-filter.ps1` أو احذف كلمة `DuoSeatFilter` من القيمتين يدوياً، وأضف `-Uninstall` لحذف الدرايفر نهائياً. احتفظ بكيبورد ثانٍ احتياطي.

## الرفع على GitHub

```
cd DuoSeat
git init
git add .
git commit -m "DuoSeat v0.3: KMDF filter driver + user-mode service"
git branch -M main
git remote add origin https://github.com/<USER>/DuoSeat.git
git push -u origin main
```
وللإصدار: `git tag v0.3.0 && git push --tags` ← يرفق Actions ملفات exe في Releases.

## التوزيع للإنتاج

التوقيع التجريبي للتطوير فقط. للتوزيع تحتاج شهادة EV وتسجيلاً في Windows Hardware Dev Center (attestation signing)، وفحص Driver Verifier / Static Driver Verifier، وتحويل INF إلى حزمة DCH مناسبة. هذه الخطوات غير مغطاة هنا.

## الرخصة

MIT
