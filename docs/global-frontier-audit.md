# Audit initial — finalisation mondiale des frontières

## Référence vérifiée

- `main` vérifiée au 26 novembre 2026 : `e2e2ccae759f9902c1c099434b0ca1a046075a55`.
- La PR #70 (« C4.28 — unifier le raccord #4/#1 dans tous les modes ») est fusionnée sur ce SHA. La PR #71, dédiée à la comparaison mondiale, est ouverte en brouillon et reste non fusionnée.
- Le dépôt contient un outil console .NET 8 / NetTopologySuite 2.6.0 (`tools/CitySelection`) qui exécute maintenant un dépistage mondial expérimental. Il ne contient pas encore de solution moteur Godot/C#.
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

Régression de périmètre identifiée : les vues C4.24–C4.28 restent des tracés superposés à des cellules existantes. Le C# de la PR #71 exécute les quatre simplificateurs sur les chaînes de frontières mondiales, mais ne reconstruit pas encore les cellules après simplification. Il ne s’agit donc pas encore de l’algorithme final ni d’une implémentation intégrée au moteur.

## Contrôle du workflow

Le workflow unique `.github/workflows/city-selection-lab.yml` compile l’outil puis lance C4, les expériences de frontière ou la génération complète selon les fichiers modifiés. Certains parcours non-PR publient ensuite les JSON générés dans `docs/` par commit automatique. Le mode mondial ajouté pour ce chantier est isolé : il ne relance pas GeoNames / la génération de sélection et n’active pas ces étapes de publication. Le résultat est un dépistage expérimental téléchargeable comme artefact de PR.

## Portée de cette première campagne

Le comparateur et le moteur C# de dépistage exécutent les quatre fonctions sur les lignes de frontières partagées déduites des arêtes interpropriétaires du graphe complet, et calculent sommets, longueur, déviation, identité des sorties et durée. Ils ne reconstruisent pas encore les territoires ni les cellules. Par conséquent, les surfaces gagnées/perdues, cellules invalides, trous, chevauchements, perte d’adjacences et conservation des côtes après reconstruction ne sont pas mesurés ; aucune sélection définitive de méthode n’est justifiée à ce stade.

La règle universelle de fusion reste ouverte. Une simple proximité n’autorise pas la fusion. Les candidats proches doivent être comparés avec leurs cellules incidentes et validés par la topologie du graphe reconstruit avant toute fusion expérimentale.

## Résultats de la campagne mondiale exécutée

Run GitHub Actions [**45808845771**](https://github.com/Keryas777/world-conquest/actions/runs/45808845771), commit `6fb46f404d0ea8c37c4fe8542cbb02c84dc7447f` : validation JavaScript, build Release .NET 8, auto-tests synthétiques, campagne mondiale et création de l’artifact de prévisualisation réussis. Le mode ciblé n’a pas régénéré la sélection des villes et n’a rien publié dans `docs/`. L’artifact contient `index.html`, `global-frontier-results.json` et la copie du graphe.

Le graphe contient **9 895 cellules**, **243 territoires**, **238 codes propriétaires**, **26 561 arêtes** et **3 477 arêtes interpropriétaires**. La campagne a traité **341 couples territoriaux**, **3 334 composants linéaires fusionnés**, **70 595 sommets** et **214 310 km** de lignes source. Elle a pris **2,034 s** au total ; le pic mémoire du processus était **319 123 456 octets** (environ 304 MiB) sur le runner GitHub Actions. Les 3 477 arêtes interpropriétaires ont toutes été examinées ; aucune exception d’intersection ni aucun repli de fusion de lignes n’a été compté.

Les longueurs utilisent la distance haversine. Les écarts de sommets sont calculés localement en tenant compte de la latitude, mais restent unidirectionnels sommet-vers-ligne, et non une distance de Hausdorff. Les méthodes Référence 40/85 reproduisent volontairement leurs tolérances historiques en degrés ; leur effet métrique varie donc avec la latitude. Les mesures ne décrivent pas encore l’erreur surfacique ou topologique des cellules.

| Méthode | Sommets après (réduction) | Longueur après (réduction) | Écart moyen / maximum | Composants non simples ou invalides | Temps de simplification |
|---|---:|---:|---:|---:|---:|
| Référence 40 | 11 918 (83,12 %) | 202 526 km (−5,50 %) | 971 m / 4,45 km | 9 | 469 ms |
| Référence 85 | 8 750 (87,61 %) | 194 346 km (−9,32 %) | 1,97 km / 9,43 km | 17 | 186 ms |
| Mode historique « Angles protégés » | 9 460 (86,60 %) | 196 410 km (−8,35 %) | 1,72 km / 16,09 km | 12 | 209 ms |
| Adaptatif | 12 288 (82,59 %) | 203 220 km (−5,18 %) | 903 m / 3,80 km | 7 | 302 ms |

Les nombres de composants non simples/invalides portent sur les lignes de sortie, pas sur les cellules. Aucune variante n’obtient zéro composant non simple/invalide. Les quatre sorties sont exactement identiques par coordonnées sur **32/341** couples ; au moins deux méthodes divergent sur les **309** autres. Par rapport à Référence 40, Référence 85 est identique sur 38 couples, Angles protégés sur 56 et Adaptatif sur 115.

### Extrémités des segments source et jonctions candidates

Le dépistage suit **70 575 extrémités de segments d’intersection source** (les points artificiels à ±180° sont exclus) jusqu’à la ligne simplifiée la plus proche. Ce sont des diagnostics de déplacement de ligne ; ils ne prouvent pas que chaque point est un raccord topologique indispensable. Le pourcentage à moins d’un mètre mesure la conservation de coordonnées d’extrémité, pas la conservation des adjacences des cellules.

| Méthode | Extrémités à ≤1 m | Extrémités à ≤100 m | Déplacement maximal d’une extrémité |
|---|---:|---:|---:|
| Référence 40 | 12 415 / 70 575 (17,59 %) | 17 430 (24,70 %) | 4,45 km |
| Référence 85 | 9 138 / 70 575 (12,95 %) | 12 667 (17,95 %) | 10,92 km |
| Angles protégés | 9 862 / 70 575 (13,97 %) | 13 727 (19,45 %) | 16,08 km |
| Adaptatif | 12 794 / 70 575 (18,13 %) | 17 988 (25,49 %) | 3,83 km |

Le détecteur séparé a repéré **1 002 extrémités d’arêtes internes** à moins de 500 m d’une frontière : 936 transversales, 64 quasi parallèles et 2 courtes transversales. Il a formé **23 paires proches à moins de 5 km** pour examen ; aucune fusion n’est appliquée. Distance, angle et longueur sont calculés dans une projection locale tenant compte de la latitude. Ces seuils sont expérimentaux. Pour BE–LU sur les géométries du graphe, deux rapprochements ressortent à 1,86 km et 4,40 km, avec des cellules incidentes différentes. Les coordonnées des trois variantes Référence 85, Angles protégés et Adaptatif sont identiques sur ce couple, tandis que Référence 40 diffère ; les quatre versions ne sont donc pas équivalentes sur la ligne extraite du graphe, malgré leur égalité exacte dans l’échantillon visuel C4.28.

### Limites et anomalies mondiales

- **319 arêtes sur 3 477** n’ont pas d’intersection linéaire directe entre les frontières des cellules, sur **156 couples**. Les plus fortes concentrations sont SL–SO (13), CA–US (11), RS–XK et TM–UZ (9 chacune), puis BJ–TG (8). Le dépistage n’a pas reconstruit ces morceaux manquants. Cette différence est cohérente avec le générateur qui accepte certaines adjacences par recouvrement de frontières tamponnées à environ un mètre ; il faut établir une source de frontière partagée fiable avant la reconstruction.
- Les plus grands écarts sommet-source/ligne sont Référence 40 sur **MN–RU (4,45 km)**, Référence 85 sur **CF–SS (9,43 km)**, Angles protégés sur **MN–RU (16,09 km)** et Adaptatif sur **IL–PS (3,80 km)**. Le déplacement maximal des extrémités d’intersections source diffère légèrement de ces maxima, car il porte sur un autre ensemble de points.
- Un essai strict préservant toutes les extrémités de chaque pièce source (run **45807827127**) a traité **67 261 pièces de deux sommets** et produit **0 % de réduction** pour les quatre méthodes. Cela montre qu’une simplification utile doit regrouper les chaînes et gérer explicitement le déplacement/raccordement des nœuds ; déplacer les nœuds sans reconstruire et retester le graphe reste dangereux.
- La campagne n’a pas reconstruit de polygones ou cellules. Elle ne calcule pas les surfaces gagnées/perdues, la validité des cellules après reconstruction, les trous, chevauchements, côtes, îles, disparition de territoires, ni la conservation des adjacences et des jonctions multi-pays. Aucun chiffre de ce dépistage ne certifie ces invariants.

### Recommandation technique provisoire

**Adaptatif reste le candidat à tester en premier**, uniquement comme orientation : il a le plus faible écart moyen et maximum parmi les mesures ligne-vers-source, le plus faible nombre de composants non simples/invalides et le meilleur maintien des extrémités dans ce dépistage. Il conserve toutefois seulement 18,13 % des extrémités à moins d’un mètre et ne passe aucun contrôle de reconstruction polygonale. Référence 85 simplifie davantage, mais a les plus fortes pertes de fidélité et le plus de composants non simples/invalides. Le mode historique « Angles protégés » restaure un point de forte déviation ; le code ne détecte pas les angles au sens géométrique.

Cette orientation **ne sélectionne pas l’algorithme définitif**. Avant de choisir, il faut définir une construction commune des frontières partagées, reconstruire cellules et territoires, comparer séparément les surfaces gagnées et perdues, mesurer les adjacences perdues/ajoutées et valider la topologie à l’échelle mondiale. La règle universelle de fusion demeure non définie. Si les déplacements d’extrémités ne peuvent pas être reconnectés sans casser les cellules, l’adaptatif ne doit pas être retenu malgré ses mesures de lignes.

Le viewer unique (`docs/global-frontier-lab.html`) charge le graphe et le JSON générés par le workflow, affiche les cellules de référence, quatre méthodes, frontière source, diagnostics de jonctions et raccourcis vers les couples problématiques. En mode couple, seules les cellules des deux territoires sélectionnés sont dessinées. Le viewer a des commandes de zoom, pincement et déplacement tactile ; le JavaScript passe le contrôle syntaxique CI. Le viewer n’affiche pas encore de cellules reconstruites puisque le moteur ne les produit pas, et Safari iOS réel n’a pas été vérifié.
