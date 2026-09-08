# Qualification W-007 à W-010

Ce document sépare les contrôles reproductibles hors jeu des validations qui nécessitent Derail Valley. Il ne remplace pas `INDEX.md` et ne change aucun état de chantier.

## Audit des échanges request/response (W-007)

Les quatre RPC corrélés par `TicketId` sont WorkTrain, ShopQuote, ShopPurchase et LicensePurchase. Ils répondent aux peers inconnus et aux refus connus. ShopPurchase protège désormais sa réponse terminale contre une exception du callback post-commit. WorkTrain protège le débit, le spawn et la notification du spawn. ShopQuote protège son backend. LicensePurchase protège débit/acquisition/impression, mais les recherches Unity précédant le débit doivent encore être validées en jeu.

Les échanges de recovery (TrainSync, railway, train manifest et world-item manifest) ne sont pas des RPC métier : ils sont répétables, bornés et ne prolongent pas la deadline totale. JobValidate et Warehouse sont des commandes sans ticket ; leurs refus sont journalisés ou signalés par le canal de refus générique, mais leur UX doit être vérifiée en jeu.

Contrôle hors jeu : lancer `powershell -NoProfile -File tools/Validate.ps1` et vérifier les tests `RpcTerminalResponseTests`, `RpcTicketTests`, `WorldItemLoadingTests`, `TrainLoadingTests` et `LoadingRetryTests`.

## Late join (W-008)

Le railway state est appliqué seulement après validation complète des cardinalités et résolution de toutes les cibles. Tant qu'il n'est pas appliqué, le client redemande le snapshot toutes les cinq secondes, sous deadline fixe.

Les trains utilisent un manifest d'identifiants réseau. La réception seule ne suffit pas : chaque voiture doit exister avec le bon identifiant et le bon CarId. Les reprises sont limitées à 128 IDs, tournantes, et le serveur ne sert que le manifest initial du joueur. Un état partiellement présent passe par la réparation de composition.

Les customizers sont inclus dans `TrainsetSpawnPart` (restoration et peintures) et sont appliqués avec le spawn/réparation ; l'ancien état `ReadyForCustomizers` n'a aucun protocole propre. `ReadyForTiles` n'a actuellement aucune donnée Hazmat à transférer dans cette base : il ne faut pas inventer un acknowledgement vide avant qu'une source autoritaire de tiles existe.

Validation physique restante : perdre volontairement chaque paquet initial (railway snapshot, un spawn trainset, manifest) et confirmer la reprise sans prolongation au-delà de 180/900 secondes ; vérifier peinture, locomotive restaurée simple/double et voitures transportées après réparation.

## Reload en session (W-009)

Le bouton reste volontairement désactivé. Le teardown réseau est répétable et isole ses callbacks, mais la base ne fournit pas encore de transaction Unity capable de restaurer l'ancien monde après un échec de chargement. Réactiver le bouton sans rollback de scène créerait un risque de session détruite à moitié.

Scénario requis avant activation : pour host seul puis host + client, sauvegarder un marqueur visible, charger une autre sauvegarde, vérifier déconnexion contrôlée ou resynchronisation, absence de doubles abonnements, une seule instance des managers, queues/RPC vides, identité persistante conservée, puis répéter trois fois. Injecter un échec au chargement au deuxième cycle et vérifier soit retour complet à l'ancien monde, soit arrêt explicite vers le menu sans softlock.

## Reconnexion et persistance (W-010)

La suite hors jeu couvre le codec versionné, le remplacement atomique, les identités persistantes, slots, conteneurs, overflow, grants et absence de NetId sauvegardé. Le manifest des items exige l'application ou un tombstone avant l'acknowledgement de l'étape.

Matrice physique restante : déconnecter avant/après pickup, drop, rangement, mise en conteneur et destruction ; reconnecter avec le même GUID ; redémarrer le host ; vérifier exactement une occurrence de chaque PersistentId, aucun NetId de l'ancienne session réutilisé comme identité, contenus/locks restaurés et overflow présent au Lost and Found. Refaire avec réponse de binding et Create perdues.

## Collecte minimale

Pour chaque scénario conserver versions jeu/mod/protocole, GUID de session, GUID joueur, étape de chargement, IDs manquants, raison de déconnexion, durée et résultat. Aucun succès hors jeu ne doit être présenté comme une validation de physique ou de streaming Unity.
