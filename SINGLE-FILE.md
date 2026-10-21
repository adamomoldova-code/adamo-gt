# ADAMO GT — un singur fișier

Distribuția `ADAMO-GT-<versiune>-Single.exe` se poate copia pe alt calculator sau pe un stick și porni direct. Nu trebuie dezarhivat manual un folder și nu se copiază DLL-uri separat.

Versiunea 2.7.1 include fluxul CRM simplificat: parolă, cantitate și stare, apoi trimitere și imprimare. Butonul apare numai după confirmarea accesului din oficiu de către CRM; necesită configurarea integrării pe server. Aplicația face o captură a primei pagini înaintea dialogului și o trimite numai la comanda utilizatorului. Căutarea online a porturilor rămâne opțională, cu explicație și confirmare. Semnarea SignPath Foundation nu este aprobată; executabilul rămâne nesemnat. Consultați `PRIVACY.md` pentru datele locale, accesul la internet și dezinstalare.

Butonul **«Устройства»** deschide camera cu previzualizare, microfonul cu înregistrare de până la 5 secunde și redare, tastatura cu evidențierea tastelor și difuzoarele cu test separat stânga/dreapta. În versiunea 2.5.1, tastatura pornește imediat la deschiderea filei și reia testul la revenirea în fereastră, păstrând tastele verificate. Esc oprește capturarea; «Продолжить» o reia. Camera și sunetul pornesc prin butoanele lor. Utilizatorul confirmă rezultatul, care se păstrează cu data și numele dispozitivului și intră în raportul JSON. Fotografiile, sunetul și succesiunea tastelor nu se salvează. Înregistrarea audio rămâne în memorie până la închiderea ferestrei. Istoricul acestor teste are propriul fișier `.peripherals` lângă istoricul SSD/GPU și nu modifică decizia de pornire a benchmarkurilor.

La pornire, componentele incluse sunt extrase automat în `%LOCALAPPDATA%\ADAMO GT\Runtime`. La următoarele porniri, fișierele verificate sunt reutilizate. Dacă lipsesc sau sunt modificate, se pregătește o copie nouă, fără a suprascrie fișierele altei instanțe pornite. Extragerea nu cere internet. Rapoartele și informațiile salvate anterior rămân în locațiile aplicației.

Este un singur fișier de distribuit; la rulare există fișiere auxiliare în cache. Acestea sunt păstrate pentru relansarea cu drepturi de administrator, inclusiv din butonul „Датчики”. Datele din cache nu sunt șterse automat la închiderea primei ferestre.

Rămân aceleași cerințe: Windows x64 și .NET Framework 4.8 sau 4.8.1. PawnIO este necesar pentru unele temperaturi; pachetul unic nu instalează automat acest driver. Arhiva obișnuită cu folder rămâne disponibilă pentru configurarea directă a `data/benchmarks.json`; pachetul unic verifică și reface componentele incluse, inclusiv configurația sa standard.

Fiecare pornire obișnuită solicită drepturi de administrator prin fereastra UAC Windows, indiferent dacă PawnIO este instalat. Dacă aplicația este deja administrator, nu repetă solicitarea. Dacă solicitarea este refuzată, aplicația continuă fără aceste drepturi și explică lângă temperatură cum se activează accesul. Un senzor GPU absent din driver nu este înlocuit cu temperatura CPU. Pentru probe din linia de comandă, ridicarea drepturilor este explicită prin `--elevate`; `--no-elevation` permite o lansare diagnostică fără solicitare.

În distribuția 2.4.2, lansatorul solicită UAC **înainte de extragerea componentelor**. Această ordine corectează o problemă descoperită în candidatul 2.4.1: relansarea executabilului intern din cache putea eșua cu o eroare Windows de cale/fișier negăsit, deși elevarea executabilului exterior funcționa. Fluxul comun de pornire păstrează revenirea fără administrator după anulare și evită cereri UAC repetate. Candidații 2.4.0 și 2.4.1 sunt păstrați separat, fără suprascriere.

## Rezultatele testelor

La prima verificare a calculatorului, programul arată caracteristicile și apoi execută testele eligibile SSD și GPU, pe rând. La următoarele porniri pe același calculator, arată rezultatele salvate și data fiecărui test, fără să repete automat sarcina. Temperaturile, inventarul, SMART și bateria continuă să fie citite din nou. Butoanele de test repetă manual SSD sau GPU; un test anulat ori nereușit nu șterge rezultatul reușit anterior și nu îi schimbă data.

Istoricul aparține calculatorului și este comun conturilor Windows care au acces la el: `%PROGRAMDATA%\ADAMO GT\History\<identificator-calculator-hash>.json`. Este separat de componentele extrase în `%LOCALAPPDATA%\ADAMO GT\Runtime`, astfel încât o versiune nouă sau altă cale a EXE-ului nu resetează testele. Fără administrator, citirea rămâne posibilă când permisiunile Windows o permit; aplicația comunică situațiile în care nu poate citi sau salva istoricul.

Un dispozitiv nou ori o configurație schimbată pe calculatorul deja verificat nu pornește automat alte teste. Rezultatele incompatibile nu sunt reutilizate, iar verificarea nouă se pornește manual. Dacă identitatea fizică/seria SSD nu este disponibilă, vitezele lui vechi nu se restaurează doar după nume. GPU este asociat după modelul hardware, fără număr de serie: înlocuirea cu alt exemplar identic nu poate fi detectată sigur; după o înlocuire, repetă manual testul. O asociere ambiguă între mai multe adaptoare nu este folosită.

Istoricul corupt, inaccesibil sau ocupat nu declanșează automat o sarcină nouă. Programul păstrează citirea caracteristicilor și indică faptul că testele pot fi pornite manual. Marcarea primei încercări împiedică repetarea automată a SSD-ului atunci când, de exemplu, GPU nu suportă testul.

## Aspect

Sigla și pictogramele de acțiuni provin din proiectul găsit în chatul „APK ADAMO”, păstrând identitatea [adamo.md](https://adamo.md/ro). În 2.4.3, informațiile, titlurile și comenzile folosesc culori neutre pentru citire mai ușoară. Verdele rămâne în siglă, conturul de focus și bara mică a resursei SSD. Antetul afișează numai sigla lângă modelul calculatorului; denumirea ADAMO GT rămâne în titlul ferestrei și rapoarte.

Sigla din aplicație are fundal transparent, iar ICO-ul executabilului include șapte dimensiuni. Textul și selectoarele au contrast revizuit; antetele compacte sunt CPU / RAM / GPU.

Subsolul afișează **Creat cu ❤️ în Moldova | ADAMO.MD**, la 12 px, într-un gri lizibil. Mărimea textelor cu date nu a fost redusă.

## Verificarea versiunii

Versiunea 2.5.1 corectează pornirea camerei, reluarea tastaturii și izolarea comenzilor între file. Regresiile automate și proba opțională pe dispozitive reale sunt consemnate în `TEST-REPORT-2.5.1.md`. Calitatea imaginii, înregistrării și difuzoarelor se confirmă manual pe fiecare calculator. Interfața principală, nemodificată în această corecție, a trecut anterior 224 de combinații de rezoluție/DPI și 24 de redimensionări, documentate în `TEST-REPORT-2.5.0.md`.

Verificarea dimensiunilor și scalării, inclusiv corecțiile din 2.4.4, este consemnată în `TEST-REPORT-2.4.4.md`. Revizia culorilor este în `TEST-REPORT-2.4.3.md`; probele automate și fizice ale funcțiilor rămân documentate în `TEST-REPORT-2.4.2.md`. Previzualizările folosesc datele reale salvate anterior, fără benchmark nou. Măsurările SSD/GPU păstrate din 2.4.1 sunt identificate prin versiune și data testului. Limitele hardware, inclusiv temperatura indisponibilă a actualului Iris Xe și matricea/niții neconfirmați pentru AUO7A99, sunt descrise în `COMPATIBILITY.md`.

În 2.4.4, textele au spațiu revizuit pentru formatarea Windows la DPI ridicat, aplicația activează adaptarea WPF la DPI-ul fiecărui monitor, iar dialogul de senzori își limitează dimensiunile la suprafața disponibilă. Ferestrele foarte mici păstrează toate cardurile prin micșorare; pentru mărimea normală a datelor sunt necesari minimum 960 × 680 DIP în interiorul ferestrei. Încadrarea singură nu garantează citirea confortabilă pe un ecran mic cu scalare mare.

## Construire din surse

1. `./build.ps1` — construiește aplicația în `dist`.
2. `./test.ps1` — verifică aplicația; unele verificări locale citesc asocierea volumului cu discul. `./test.ps1 -PeripheralsOnly` verifică separat noua funcție, folosind dispozitive și semnale simulate, fără activarea camerei, microfonului sau difuzoarelor.
   Proba fizică opțională: `./tests/live/Test-LivePeripherals.ps1 -ActivateHardware`. Activează temporar camera, înregistrează și redă 5 secunde de microfon, apoi testează ambele canale audio. Salvează numai diagnostice numerice și nu confirmă automat că dispozitivele sunt bune.
3. `./test-single-file.ps1` — testează pachetul și lansatorul fără teste de sarcină.
4. `./build-single-file.ps1` — creează executabilul unic în `releases`.
5. `./package.ps1` — creează separat arhivele aplicației și surselor.

Scripturile de împachetare refuză suprascrierea unei versiuni existente. Pentru o probă nouă se poate indica un alt `-OutputDirectory`.
