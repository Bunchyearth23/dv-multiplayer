# Campagne réseau et VR

Lot local du 6 septembre 2026, rattaché à W-006, X-008, X-010 et I-001. Aucun état W changé. Aucune session Unity, VR ou multi-clients exécutée pour ce lot.

## Implémentation vérifiable hors Unity

`TickSnapshotBuffer<T>` conserve une FIFO par component : 256 entrées au maximum, 32 applications par tick dans `TickedQueue<T>`. Aucun snapshot accepté n’est coalescé : les bogies transportent aussi des changements de track et des déraillements. Les doublons et ticks anciens sont ignorés ; la comparaison utilise une différence signée pour accepter le wrap de `uint`. Un saut d’au moins une demi-plage est ambigu et rejeté. `Clear` retire les entrées en attente sans réautoriser les ticks anciens ; `Reset` remet aussi le watermark et les compteurs à zéro au changement de lifecycle.

Un overflow refuse atomiquement la nouvelle entrée et emprunte `FailWorldSync`, qui arrête la session client avec un diagnostic. Une exception pendant l’application emprunte également ce parcours. Ce choix borne la mémoire sans supprimer silencieusement une transition. Il ne constitue pas un mécanisme de recovery ni un buffer d’interpolation adaptatif. Le budget est par component, pas global : beaucoup de trains peuvent encore dépasser le frame budget. L’extrapolation des bogies reste proportionnelle à l’âge du snapshot. Les queues séparées ne fournissent pas un ordre global entre vitesse, bogies et autres packets métier ; ce lot ne modifie pas les canaux de ces packets.

`NetworkedPlayer.HoldItem(..., rightHand)` et `DropItem(rightHand)` gèrent désormais les deux mains indépendamment, leurs offsets et leurs colliders. Un transfert du même objet libère l’ancienne main ; deux objets distincts peuvent être présentés simultanément. `ReleaseItems` libère les deux. Les états enabled des colliders et interactionAllowed sont mémorisés avant suspension et restaurés après la remise en physique. Les poses VR sont appliquées dans `LateUpdate`, après le calcul du tracking ; le fallback non-VR utilise des anchors symétriques. Cela ne définit pas les offsets de grip de chaque prefab ni un solver à deux grips pour un seul objet.

`LocalPlayerTrackerVR` émet une pose complète chaque seconde, même pour des mains immobiles. Le receiver attend les positions et rotations des deux mains avant d’activer l’IK. Cela améliore le bootstrap et la récupération des deltas perdus, sans garantir une livraison sous perte continue. Une seule manette disponible ne suffit actuellement pas à initialiser cet IK.

**Limite de raccordement :** les appels actuels depuis `NetworkedItem` et `NetworkedPluggableObject` utilisent toujours la main droite par défaut. Le payload d’ownership n’identifie pas encore la main physique. Le parcours gauche/dual hand de bout en bout reste donc à implémenter et valider ; les nouvelles méthodes de présentation ne suffisent pas à le déclarer disponible en session. Les équipements, customizers et interactions natives VR ne sont pas qualifiés par ce lot.

## Mesures disponibles

Le watcher écrit une ligne `network_campaign` toutes les dix secondes, avec des compteurs cumulés depuis sa création : nombre de frames, moyenne et maximum de `Time.unscaledDeltaTime` en ms, nombre de contrôles de position, demandes de hard correction au-delà de 2 m, maximum du delta en mètres, packets visant un trainset inconnu ou une composition divergente, et taille du managed heap obtenue sans GC forcé. Une demande de correction n’est pas une correction appliquée. La mémoire mesurée exclut les allocations natives Unity et GPU. Les compteurs de divergence décrivent des packets observés, pas des trains uniques ni un checksum complet du monde.

Chaque queue active écrit au plus une ligne `train_queue` toutes les dix secondes : identifiant, profondeur, high-water mark, snapshots anciens/dupliqués, overflows et applications réussies. Les compteurs de queue repartent à zéro quand le component est désactivé. Ils ne mesurent ni la durée physique de chaque application ni sa convergence. Un overflow est aussi indiqué dans le diagnostic de déconnexion. Les logs ont un coût à mesurer avec beaucoup de trains.

Le trafic sortant est désormais compté par peer et type concret de packet, et l'entrant par peer, canal et méthode de livraison. La cardinalité est bornée à 512 séries ; les suivantes alimentent `dropped_series`. Un histogramme borné expose p50/p95/p99 de frame time. La ligne de campagne contient mémoire managée et mémoire native rapportée par Unity. Restent RTT/jitter/pertes observés, taux d'erreurs, corrections réellement appliquées et comparaison autoritaire des items/jobs/économie.

## Harness et campagne à exécuter

Lancer `powershell -NoProfile -File tools/Validate.ps1`. Le runner exécute tous les tests même après un échec, affiche chaque résultat, le total d’échecs et la durée, puis retourne un code non nul si nécessaire. Les nouveaux tests utilisent les sources de production : admission FIFO atomique, ticks désordonnés, wrap, Clear/Reset, budget de drain et exception, 100 000 snapshots synthétiques, métriques avec valeurs invalides, et round-trip/merge des poses des deux mains. Ils n’exécutent pas `MonoBehaviour`, l’IK, les colliders, les transports ni la physique Unity.

Pour la campagne réelle, enregistrer le build, les versions de jeu/mods, le transport, le matériel et les réglages avant chaque run. Conserver les logs de chaque client et du host avec des horloges corrélées. Comparer une baseline locale puis RTT 50/150/300 ms, jitter 30/80 ms et pertes 1/5 %, séparément puis combinés. Les nombres de joueurs, durées et budgets restent à arbitrer selon Q-001. Voir [RELEASE-QUALIFICATION.md](RELEASE-QUALIFICATION.md) pour la matrice, les preuves et le rollback.

| Parcours | Observation attendue, à vérifier en session |
| --- | --- |
| Train en mouvement, freinage, aiguillage puis déraillement sous rafale | Ordre des transitions conservé ; profondeur de queue et coût d’extrapolation relevés ; déconnexion explicite en overflow. |
| Passager VR et non-VR sur train, hard correction | Déplacement cohérent avec le train, absence de saut durable des mains/items ; distinguer correction demandée et appliquée. |
| Late join avec contrôleurs immobiles, puis perte et reprise des packets | Réception d’une pose complète, initialization IK et convergence après reprise. |
| Objets distincts gauche/droite, transfert et rangement | Après raccordement du payload de main : une seule possession par objet, autre main préservée, colliders restaurés ; vérifier aussi modèle changé et déconnexion. |
| Un objet tenu à deux mains, équipements et customizers | Fonctionnalités encore à compléter ; ne pas considérer ce parcours comme validé. |
| Session longue avec nombreux trains et churn | Profondeurs bornées, mémoire et frame time suivis, logs conservés ; aucun seuil de capacité annoncé sans mesure. |

Validation finale de ce lot sur le dossier partagé : tools/Validate.ps1 retourne 0 ; les quatre projets compilent et 116 tests passent, 0 échec. Ce total inclut les ajouts des autres chantiers et sept tests propres à W-006. Le log local est conservé dans tools/validation-latest.log. Aucun fichier installé dans le jeu ; aucune mesure de session réelle produite.
