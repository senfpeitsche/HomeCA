# Intermediate-CA rotieren

Lege unter derselben Root-CA eine Ersatz-Intermediate-CA an, mache sie zur ausstellenden CA und verteile neu ausgestellte Zertifikatsketten.

> Eine CA wird nur nach einem Sicherheitsvorfall gesperrt. Bei einer normalen Rotation die alte Intermediate erst nach Ablauf oder Ersatz ihrer Zertifikate deaktivieren.

## Ablauf der Rotation

1. Die Ersatz-Intermediate unter der vorhandenen Root-CA anlegen.
2. Die Ersatz-CA als aktive Ausstellungs-CA festlegen und Zertifikate für jedes Ziel neu ausstellen oder erneuern.
3. Die neuen Zertifikatsketten auf den Zielsystemen verteilen.
4. Die bisherige Intermediate und ihre CRL erreichbar lassen, bis alle von ihr ausgestellten Zertifikate abgelaufen oder ersetzt sind.

Bei einer normalen Intermediate-Rotation bleibt die Root-CA unverändert; Clients benötigen daher keine neue Vertrauensanker-Installation. Eine CA-Sperrung ist einem kompromittierten CA-Schlüssel vorbehalten und erfordert den Ersatz aller von ihr ausgestellten Zertifikate.

Nach dem Ausrollen ein erneuertes Dienstzertifikat prüfen und bestätigen, dass es zur Ersatz-Intermediate-CA verkettet ist. Das bisherige Zertifikatsinventar weiter überwachen, bis die Bedingungen für die Außerbetriebnahme erfüllt sind.

Die Rotation früh genug planen, damit neu ausgestellte Zertifikate ihre vorgesehene vollständige Laufzeit erhalten können, bevor die aktuelle Intermediate-CA abläuft.

Den CRL-Endpunkt der bisherigen Intermediate-CA nicht entfernen, solange von ihr ausgestellte Zertifikate noch verwendet werden.

Vor dem ersten erneuerten Zertifikat bestätigen, dass die Ersatz-CA die aktive Ausstellungs-CA ist.

Bei Verdacht auf einen kompromittierten CA-Schlüssel die betroffene CA sperren, Ersatz ausstellen und die Ausrollung auf exponierte Dienste priorisieren.

Das Zertifikatsinventar verwenden, um jedes von der ausgemusterten oder gesperrten CA ausgestellte Zertifikat zu identifizieren.

Dienstverantwortliche vor dem Wechsel des aktiven Issuers über das Rotationsfenster und die erforderliche Kettenaktualisierung informieren.
