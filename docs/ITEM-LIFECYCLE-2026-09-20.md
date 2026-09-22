# Objets client : duplication et interactions — 20 septembre 2026

## Signalement et preuves

Le joueur qui rejoint voit des objets en double et peut perdre les interactions après avoir pris des objets au station office. Le Player.log local du 20 septembre est celui de l'hôte : on y voit des prises et lâchers reçus du client, mais il ne prouve pas la pile d'exception du client. Le lien exact avec le blocage rapporté reste à confirmer en session réelle.

Défauts vérifiés dans le chemin exécuté :

- SyncWorldState appelle CacheWorldItems une seule fois. Le filtre excluait tous les objets essentiels, même non possédés et posés dans le monde. Aucun traitement des objets locaux ajoutés après cette passe.
- SendToCache détruisait RespawnOnDrop. Le code natif installé de StorageController.AddItemToStorageItemList appelle GetComponent<RespawnOnDrop>().UpdateSpawnParams() sans garde. Réutiliser un objet du cache puis le ranger peut donc échouer après modification partielle de l'état natif.
- SendToCache désactivait directement l'objet. Les corrections dropped/attached/remote-owned utilisaient uniquement Inventory.DropItemFromHandsOrInventory ; cette méthode native retourne sans libérer une prise directe lorsque l'objet n'est ni équipé ni indexé dans l'inventaire.

## Correction

Le cache conserve RespawnOnDrop et ses callbacks de stockage. L'ancien transpiler RespawnOnDropPatch était désactivé par un retour immédiat ; il est remplacé par des préfixes sur Checker et RespawnOrDestroy : leurs routines sont vides sur un client distant actif, natives sur l'hôte ou hors session. Initialisation, métadonnées et listeners natifs restent disponibles.

La prise native est explicitement terminée avant retrait de l'inventaire, mise en cache, correction de position, attachement ou présentation dans une main distante. Un accusé concernant l'objet déjà tenu par le joueur local conserve le chemin existant qui préserve sa prise.

La passe initiale retire aussi les objets essentiels non possédés. Les objets de l'inventaire, conteneurs, objets personnels essentiels, objets tenus, restaurations dormantes, objets déjà liés par NetId et documents gérés par jobs sont préservés. NetworkedItem.Start appelle le même filtre après initialisation/binding : les objets du monde chargés tardivement sont mis en cache sans ajouter de scan périodique. Une entrée déjà retirée du cache ne peut pas être réutilisée une seconde fois.

Tout ce traitement reste sur Unity : interactions, composants, inventaire et activation ne sont pas déplacés sur un worker. Aucun changement du protocole, du journal persistant ou de l'autorité économique.

## Validation hors jeu

Validation canonique tools/Validate.ps1 réussie : compilation net48 contre le jeu installé, 154 tests protocole, 11 contrôles Harmony portefeuille, fixture backend achat et 17 nouveaux contrôles du cycle des objets. Les tests nouveaux compilent les méthodes de production du cache et de libération et exécutent les vrais patchs Harmony sur des surfaces natives simulées. Ils couvrent réutilisation, contrat de stockage, doublons essentiels/streaming, protection des inventaires/jobs/replicas, tombstone répété, prise directe sans inventaire et séparation client/hôte/hors session. Onze avertissements de compilation existants restent présents.

## Qualification Unity restante

Installer un candidat coordonné sur les deux joueurs, puis rejoindre près d'un station office et comparer le nombre d'objets. Prendre, ranger, lâcher et reprendre plusieurs objets ; vérifier ensuite portes, leviers et autres interactions. Tester deux prises concurrentes, sortie/retour de zone pendant une prise, arrivée dans une nouvelle station, reconnexion, save/reload et VR/non-VR. Vérifier inventaire personnel, livrets de jobs, stock boutique et absence de réapparition locale autonome. Conserver les Player.log des deux joueurs.

Aucun candidat construit ou activé pour cette correction ; aucun gain de frame time ou résultat de gameplay revendiqué.
