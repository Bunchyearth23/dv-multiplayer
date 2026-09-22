# F7 distant en attente de l'hôte — 20 septembre 2026

## Cause confirmée

Le Player.log de l'hôte contient dix refus corrélés à `state-b3e2aeb03b864781805900c83c1a931e` : `peer=2`, `status=Rejected`, `code=unknown-peer`, `The sender is not a current server peer.` La requête arrive donc au raccordement BDVM. Ce n'est pas une absence de demande F7 ni une dépendance HTTP.

`MultiplayerServerProtocolAdapter.Receive` exige que `server.GetPlayer(sender.PlayerId) == sender`, puis résout son identité persistante et sa readiness. `NetworkServer.RegisterExternalSerializablePacket` créait un nouveau `ServerPlayerWrapper` à chaque paquet, différent du wrapper enregistré. La condition échouait avant la résolution persistante. Le raccordement n'envoie pas de réponse à un pair non reconnu : les retries du client répètent le refus jusqu'au timeout.

## Correctif

Le callback des paquets sérialisables utilise désormais `GetWrapper(player)`, comme les paquets externes ordinaires. Les méthodes d'enregistrement et le cache sont regroupés dans `NetworkServer.ExternalPackets.cs`, compilé dans Multiplayer et dans le test ciblé. Les contrôles BDVM d'identité, readiness, permissions, version et idempotence ne sont pas modifiés. Le lien peer → joueur reste fourni par le transport serveur, jamais par le contenu du paquet.

Aucun nouveau scan, worker, accès Unity déporté ou changement de protocole. Le cache de wrappers existant reste invalidé à la déconnexion et au stop.

## Preuves hors jeu

Le test sur les callbacks de production et la vraie sérialisation LiteNetLib reproduit l'échec avant correction (`sender differs from GetPlayer`) et passe après correction. Huit contrôles couvrent identité de référence, identité persistante, paquets ordinaires, payload compressé, déconnexion, reconnexion avec réutilisation de PlayerId, ancien peer et peer inconnu.

Validation canonique `tools/Validate.ps1` réussie : 154 tests protocole, 11 contrôles Harmony shop, fixture backend achat, 17 contrôles du cycle des objets et 8 contrôles d'identité des paquets externes. Compilation contre les bibliothèques du jeu réussie, avertissements existants seulement. Le premier correctif des objets reste présent et validé.

## Qualification restante

Correctif non installé. Après déploiement du même candidat coordonné sur hôte et invité : ouvrir F7, vérifier l'affichage du compte et des données autorisées ; rafraîchir, fermer/réouvrir, exécuter une action autorisée et reconnecter. Le journal de l'hôte doit montrer des réponses `module-state-page` pour l'invité, sans refus `unknown-peer`. Vérifier aussi refus explicite d'une action non autorisée. Ces tests hors jeu ne prouvent ni le rendu Unity ni une session distante réelle.
