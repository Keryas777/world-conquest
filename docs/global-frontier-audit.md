# Audit initial — finalisation mondiale des frontières

## Référence vérifiée

- `main` vérifiée au 26 novembre 2026 : `e2e2ccae759f9902c1c099434b0ca1a046075a55`.
- La PR #70 (« C4.28 — unifier le raccord #4/#1 dans tous les modes ») est fusionnée sur ce SHA. Aucune PR n’est ouverte pour la finalisation mondiale.
- Le dépôt contient un outil console .NET 8 / NetTopologySuite 2.6.0 (`tools/CitySelection`). Il ne contient pas encore de solution moteur Godot/C#.
- La donnée publiée `docs/voronoi-graph.json` contient **9 895 cellules**, **243 territoires**, **238 propriétaires**, **26 561 arêtes** et **3 477 arêtes interpropriétaires**, réparties en **341 couples de territoires propriétaires adjacents**.
- `docs/world-selection-C.json` confirme lui aussi 9 895 villes dans 242 pays, plus le territoire géographique `XC`. La spécification jointe indique 9 894 cellules. Cette différence d’une cellule est antérieure au présent chantier et n’est pas corrigée par celui-ci.

## Audit C4.24 à C4.28

| Mode encore candidat | Calcul exécuté dans le viewer C4.28 | Paramètres / limite constatée |
|---|---|---|
| Référence 40 | Douglas–Peucker récursif sur les coordonnées longitude/latitude brutes | Tolérance euclidienne de 0,040° ; échelle est-ouest dépendante de la latitude. |
| Référence 85 | Même Douglas–Peucker brut | Tolérance de 0,085° ; même dépendance à la latitude. |
| Angles protégés | Douglas–Peucker métrique, puis ajout d’au plus un point de plus forte déviation par segment | Base 10,5 km ; restauration à partir de 6,3 km. Le code ne détecte pas un angle au sens géométrique : il restaure une déviation maximale. |
| Adaptatif | Douglas–Peucker métrique à 6,5 km, puis simplification de chaque intervalle à 3,8 km | Les points du premier passage deviennent des ancres protégées. |

Les méthodes Renforcé et Droites sont encore visibles dans les contrôles historiques de C4.28. Elles ne sont pas retenues ici comme candidates. Le bouton Fusion de ce viewer est un alias de Renforcé ; il ne constitue pas un cinquième mode de simplification.

La projection métrique locale de C4.28 est une équirectangulaire simplifiée, avec une latitude moyenne unique : `x = longitude × 111 320 × cos(latitude moyenne)`, `y = latitude × 111 320`. Elle convient au petit cas belge-luxembourgeois, mais ne traite pas le monde, les hautes latitudes ou l’antiméridien.

### Vérification numérique BE–LU

Le calcul a été rejoué depuis les fonctions du viewer C4.28 et le JSON de référence du dépôt. Les quatre méthodes produisent **exactement les mêmes coordonnées**, pas seulement le même nombre de sommets.

- Les trois sous-tracés d’entrée ont respectivement 46, 51 et 14 points, soit 111 points avant suppression des deux raccords communs.
- Les quatre sorties ont 3, 3 et 2 points par sous-tracé ; le tracé concaténé contient 6 sommets distincts.
- La longueur des trois sous-tracés simplifiés totalise environ 91,33 km. Le maximum de déviation observé entre les sommets source et la ligne simplifiée est inférieur à 2,97 km.
- La fusion locale retenue place les raccords #4 et #1 au point `[5.88546, 49.64353]`. Les jonctions initiales sont distantes d’environ 4,38 km.
- Les jonctions #1 et #4 proviennent de paires de cellules différentes : `2960316/2960634` côté LU et `2787315/2792442` côté BE. Le viewer les réunit visuellement sans reconstruire les cellules ni démontrer que cette connexion topologique peut être fusionnée dans le graphe de jeu.

La comparaison C4.28 est donc numériquement reproductible sur l’échantillon, mais ne départage pas les quatre candidates. La raison mesurée est que tous les points éliminés restent sous la tolérance la plus stricte ; les autres règles ne réintroduisent aucun point.

## Détection des jonctions et des régressions

Les historiques C4.17–C4.23 ont ajouté des diagnostics par distance, longueur et angle, mais `BoundaryNodeChainLab.cs` exécute encore uniquement la paire BE–LU. Son champ `junctions` est toujours un tableau vide ; les 12 `endpointDiagnostics` exportés sont les 12 extrémités les plus proches de la frontière, pas une détection mondiale exhaustive des jonctions.

C4.28 sélectionne les repères par leur rang dans cette liste locale (#1, #3, #4). Il choisit la jonction fusionnée en prenant l’inflexion la plus intérieure d’un segment de frontière ; à défaut, il prend son milieu curviligne. Il ne vérifie ni la redondance du graphe Voronoï, ni la conservation des adjacences après fusion. Le point est calculé sur la frontière source, sans déplacement systématique vers l’intérieur d’un territoire.

Régression de périmètre identifiée : les vues C4.24–C4.28 restent des tracés superposés à des cellules existantes. Aucune ne reconstruit les cellules après simplification. Le C# courant ne porte donc pas encore l’algorithme de simplification mondiale demandé.

## Contrôle du workflow

Le workflow unique `.github/workflows/city-selection-lab.yml` compile l’outil puis lance C4, les expériences de frontière ou la génération complète selon les fichiers modifiés. Certains parcours non-PR publient ensuite les JSON générés dans `docs/` par commit automatique. Le mode mondial ajouté pour ce chantier est isolé : il ne relance pas GeoNames / la génération de sélection et n’active pas ces étapes de publication. Le résultat est un dépistage expérimental téléchargeable comme artefact de PR.

## Portée de cette première campagne

Le comparateur et le moteur C# de dépistage exécutent les quatre fonctions sur les lignes de frontières partagées déduites des arêtes interpropriétaires du graphe complet, et calculent sommets, longueur, déviation, identité des sorties et durée. Ils ne reconstruisent pas encore les territoires ni les cellules. Par conséquent, les surfaces gagnées/perdues, cellules invalides, trous, chevauchements, perte d’adjacences et conservation des côtes après reconstruction ne sont pas mesurés ; aucune sélection définitive de méthode n’est justifiée à ce stade.

La règle universelle de fusion reste ouverte. Une simple proximité n’autorise pas la fusion. Les candidats proches doivent être comparés avec leurs cellules incidentes et validés par la topologie du graphe reconstruit avant toute fusion expérimentale.

## Résultats de la campagne mondiale exécutée

Run GitHub Actions **45805525058**, commit `83b5056a437a31866845e31664a54fa5bd5f5c78` : build Release réussi, auto-tests synthétiques réussis et génération mondiale réussie. Les premiers échecs de CI ont révélé puis corrigé deux défauts du laboratoire : API indisponible en .NET 8 et récursion infinie pour les géométries ponctuelles. Le workflow final n’a ni régénéré la sélection des villes ni publié de fichiers dans `docs/`.

Le graphe audité compte **9 895 cellules**, **243 territoires**, **238 codes propriétaires**, **26 561 arêtes** et **3 477 arêtes interpropriétaires**. Le dépistage a traité **341 couples territoriaux**, issus de **3 334 composants linéaires** et **70 595 sommets source**, représentant **214 310 km** de frontières extraites. Le traitement complet a pris **1,58 s** et le pic de mémoire du processus .NET a été **314 335 232 octets** (environ 300 MiB), sur le runner GitHub Actions.

Les longueurs utilisent la distance haversine. L’écart source/sortie est la distance de chaque sommet source à la ligne simplifiée, dans une projection équirectangulaire locale par composant, corrigée par le cosinus de sa latitude moyenne. C’est un écart unidirectionnel sommet-vers-ligne, pas une distance de Hausdorff. Les tolérances de 0,040° et 0,085° restent exprimées en degrés. Les mesures ne décrivent donc pas encore une erreur surfacique ou topologique des cellules.

| Méthode | Sommets après (réduction) | Longueur après (réduction) | Écart moyen / maximum | Composants non simples ou invalides | Temps de simplification |
|---|---:|---:|---:|---:|---:|
| Référence 40 | 11 918 (83,12 %) | 202 526 km (−5,50 %) | 971 m / 4,45 km | 9 | 129 ms |
| Référence 85 | 8 750 (87,61 %) | 194 346 km (−9,32 %) | 1,97 km / 9,43 km | 17 | 93 ms |
| Angles protégés | 9 460 (86,60 %) | 196 410 km (−8,35 %) | 1,72 km / 16,09 km | 12 | 106 ms |
| Adaptatif | 12 288 (82,59 %) | 203 220 km (−5,18 %) | 903 m / 3,80 km | 7 | 132 ms |

Les nombres de composants non simples/invalides portent sur les LineStrings de sortie, pas sur les cellules. Aucune variante ne produit zéro défaut de simplicité. Les comparaisons géométriques par coordonnées sont identiques pour les quatre méthodes sur **32/341** couples ; il existe au moins une divergence entre méthodes sur **309/341** couples. Par rapport à Référence 40, les sorties sont identiques pour Référence 85 sur 38 couples, Angles protégés sur 56 et Adaptatif sur 115.

Le cas BE–LU confirme que la reconstruction de la ligne source modifie le diagnostic : sur les 3 tracés C4.28, les quatre méthodes avaient donné exactement les mêmes coordonnées (6 sommets raccordés). Sur la frontière extraite des cellules du graphe, on obtient 7 composants et 100 sommets source ; Référence 40 produit 16 sommets, les trois autres 15, avec des coordonnées qui diffèrent. Les candidats de fusion mesurés comprennent deux rapprochements BE/LU séparés de 1,86 km et 4,40 km, issus de paires de cellules incidentes différentes. Aucun n’est fusionné.

### Anomalies à traiter avant la reconstruction

- **319 arêtes du graphe sur 3 477 n’ont pas d’intersection linéaire directe** entre les frontières des cellules ; elles concernent **156 couples territoriaux**. Le générateur existant accepte certaines adjacences par recouvrement de frontières tamponnées à environ un mètre. Le dépistage ne reconstruit pas ces 319 segments manquants : c’est un point de raccordement des données à résoudre avant de prétendre à un réseau mondial complet. Aucun overlay n’a échoué par exception.
- La plus forte concentration d’arêtes sans intersection directe est **SL–SO (13)**, puis **CA–US (11)**, **TM–UZ (9)**, **RS–XK (9)** et **BJ–TG (8)**.
- Les plus grands écarts mesurés sont Référence 40 sur **MN–RU (4,45 km)**, Référence 85 sur **CF–SS (9,43 km)**, Angles protégés sur **MN–RU (16,09 km)** et Adaptatif sur **IL–PS (3,80 km)**.
- Le détecteur expérimental a relevé **1 002 extrémités** proches d’une frontière (936 transversales, 64 quasi parallèles et 2 courtes transversales), ainsi que **23 paires d’incidence à moins de 5 km**. Ces classes dépendent des seuils de dépistage et ne constituent pas une décision de fusion.

### Recommandation technique provisoire

Pour la suite de l’expérimentation C#, **Adaptatif est le meilleur candidat à tester d’abord** : il conserve le plus de fidélité selon les mesures disponibles (écart moyen 903 m, maximum 3,80 km), réduit presque autant de sommets que Référence 40 et présente le plus petit nombre de composants non simples. Référence 85 réduit davantage les sommets mais déforme plus et produit plus de composants non simples. Angles protégés a le pire écart maximum et son intitulé ne correspond pas à son calcul réel.

Cette recommandation reste une **orientation**, pas la sélection définitive demandée. Il manque la reconstruction des cellules, le contrôle des surfaces gagnées/perdues, l’inventaire des adjacences conservées/perdues et les vérifications monde entier de validité, trous, chevauchements, côtes, îles et territoires. La fusion universelle est également non définie. Ces contrôles sont prioritaires sur le gain de simplification.

Le viewer unique charge le graphe et le JSON mondial généré par le workflow, montre source et quatre variantes, et facilite l’accès aux couples ayant des contacts linéaires manquants, des composants non simples ou les plus forts écarts. En mode couple, il ne dessine que les cellules des deux territoires sélectionnés afin de réduire le travail du canvas. La compatibilité iOS a été préparée avec les événements tactiles Pointer Events et commandes accessibles, mais n’a pas été vérifiée sur un appareil Safari réel ; aucune validation manuelle iPhone n’a été demandée à ce stade.

