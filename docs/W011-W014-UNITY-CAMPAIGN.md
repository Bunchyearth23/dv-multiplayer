# Campagne Unity W-011 à W-014

Cette campagne complète les tests hors jeu. Elle ne doit être déclarée réussie qu'avec un host et deux clients sur le même build et le même ensemble de mods. Conserver le log de chaque instance, l'identité persistante des joueurs, l'heure UTC du scénario et le résultat attendu/observé. Aucun scénario ci-dessous n'a été exécuté pendant l'audit.

## Préparation commune

1. Créer une sauvegarde dédiée avec deux profils clients persistants A et B, un wallet connu et un magasin avec stock connu.
2. Lancer host, A puis B ; attendre `Complete` avant toute action.
3. Répéter chaque conflit avec A puis B initiateur, puis avec perte de la réponse et reconnexion de l'initiateur.
4. Après chaque scénario, comparer sur les trois instances : owner, état/slot/conteneur/transform des items, wallet, stock, licences, composition/attelages/cargo/jobs et branche des aiguillages.
5. Un test échoue en cas d'exception, softlock, effet doublé, divergence après 10 s, refus sans explication exploitable ou résidu après reconnexion.

## W-011 — cycle autoritaire des items

| ID | Action physique | Résultat attendu |
| --- | --- | --- |
| IT-01 | A et B saisissent simultanément le même objet. | Un seul owner ; le perdant reçoit la correction autoritaire ; trois états identiques. |
| IT-02 | Owner alterne main gauche/droite, inventaire, slot puis drop. | Main et emplacement exacts convergent ; aucun clone/collider résiduel. |
| IT-03 | Insérer puis retirer un objet d'un conteneur pendant que l'autre client tente de le saisir. | Une seule transition gagne ; contenu et ownership identiques. |
| IT-04 | Attacher/détacher un objet, puis détruire l'objet ou sa cible pendant une requête concurrente. | Refus/correction sans référence morte ni réapparition. |
| IT-05 | Couper la connexion pendant pickup, drop, transfert et destruction ; reconnecter le même profil. | Ownership de session libéré, identité persistante conservée, aucune duplication/perte. |
| IT-06 | Faire late join B après chaque état ci-dessus. | B reçoit directement l'état final, pas un état intermédiaire rejoué. |

## W-012 — achats et licences

| ID | Action physique | Résultat attendu |
| --- | --- | --- |
| PU-01 | A et B achètent simultanément la dernière unité du même article. | Un achat, un débit, un spawn ; l'autre réponse indique le stock insuffisant. |
| PU-02 | Perdre la réponse après débit, puis réessayer la même opération. | Même résultat/receipt ; aucun second débit ou spawn. |
| PU-03 | Déconnecter l'acheteur après débit puis avant acknowledgement, et reconnecter. | Recovery explicite ; aucune nouvelle opération tant que l'issue n'est pas déterminée. |
| PU-04 | Acheter un panier multi-lignes dont le stock/fonds change entre quote et commit. | Revalidation atomique ; compensation complète ou achat complet. |
| LI-01 | A et B achètent simultanément la même licence avec fonds juste suffisants pour une. | Une acquisition et un document autoritaire ; débit unique. |
| LI-02 | Réessayer après réponse perdue, puis restart host et reconnexion. | Licence persistante ; aucun redébit ni second grant physique. |
| LI-03 | Tester prérequis absent, dette, hors portée et permission révoquée. | Aucun débit/document ; statut/refus lisible et corrélé. |

## W-013 — rames, réparation et fast travel

| ID | Action physique | Résultat attendu |
| --- | --- | --- |
| TR-01 | Supprimer localement un wagon replica, casser un attelage, inverser deux wagons puis demander sync. | Manifest restaure identité, ordre, liens, hoses/câbles, cargo et job. |
| TR-02 | Provoquer spawn/delete/couple pendant la réparation et late join B. | Pas de doublon ; snapshot suivant converge ou erreur explicite sans mutation supplémentaire. |
| FT-01 | Voyager avec locomotive seule, vapeur+tender, MU et rame avec wagons laissés. | Groupe natif correct, wallet débité une fois, passagers et observers convergent. |
| FT-02 | Destination non chargée chez B et origin shift pendant le voyage. | Relocation précède la reprise physique ; aucun retour à l'ancienne position. |
| FT-03 | Perdre la réponse puis répéter ; déconnecter initiateur à chaque étape. | Ledger empêche second débit/voyage ; recovery bloque une issue ambiguë. |
| FT-04 | Injecter destination occupée et échec après uncouple. | Compensation uniquement si aucun wagon n'a bougé ; sinon recovery explicite et stable. |

## W-014 — validations serveur

| ID | Action physique | Résultat attendu |
| --- | --- | --- |
| VA-01 | Depuis map/radio, commuter des aiguillages à 1, 2 et 3+ sorties ; envoyer une branche hors borne. | Branches réelles acceptées ; hors borne refusée ; état identique partout. |
| VA-02 | Demander autorité sur port valide, port inconnu et port non-control ; envoyer arrays ports désalignés/NaN. | Seul le port control valide est accepté ; pas d'exception ni croissance d'ownership parasite. |
| VA-03 | Changer fusibles près/loin du véhicule, avec ID inconnu et arrays désalignés/surdimensionnés. | Seule l'action proche et valide est relayée ; correction/refus pour les autres. |
| VA-04 | Peindre intérieur/extérieur, cible enum invalide, trop loin et sans permission service. | Seules cible, portée et permission valides modifient la rame. |
| VA-05 | Rerail sur le bon rail puis fournir le TrackId voisin avec une position trompeuse. | Le second cas doit être refusé ; tant que ce contrôle n'existe pas, W-014 reste incomplet. |
| VA-06 | Spawn en modes sandbox/career avec livery, index, track, espace, portée et permission invalides. | Policy du mode confirmée ; aucune création sur refus. |
| VA-07 | Utiliser un remote non appairé puis appairé sur un wagon éloigné. | Le remote non appairé doit être refusé ; tant que son identité n'est pas transportée, W-014 reste incomplet. |
| VA-08 | Valider/détruire un job booklet tenu par l'autre joueur. | Seul le propriétaire ou le parcours autorisé agit ; correction sans disparition concurrente. |

## Sortie et preuves

Archiver un dossier par run contenant les trois logs, la matrice remplie et la sauvegarde avant/après. Rejouer tous les P0 une seconde fois après redémarrage du host. W-011 à W-014 ne peuvent satisfaire leurs critères Unity tant que VA-05 et VA-07 ne sont pas implémentés et que tous les scénarios correspondants ne sont pas observés sur deux machines au minimum.
