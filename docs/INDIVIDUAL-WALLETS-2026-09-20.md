# Portefeuilles individuels — 20 septembre 2026

## Cause confirmée dans le code

`NetworkedSaveGameManager.Server_OnMoneyChanged` transmettait chaque changement du portefeuille natif de l'hôte à `NetworkServer.SendMoney`, qui diffusait un même `ClientboundMoneyPacket` à tous les invités. Le client l'appliquait sans identité. `ClientboundSaveGameDataPacket.CreatePacket` reprenait également le solde hôte. Enfin, les achats et services distants utilisaient encore `Inventory.Instance` sur le serveur, malgré le ledger individuel déjà disponible.

## Correction

- Suppression du hook de diffusion du solde hôte. Le solde de chargement est lu pour le joueur authentifié concerné.
- L'API des comptes individuels transmet les changements uniquement au propriétaire connecté. Un état courant est renvoyé en fin de chargement. Paquets ordonnés fiables, identité persistante explicite et montant double ; refus côté client des identités étrangères, valeurs invalides et remplacements du portefeuille hôte.
- BDVM initialise/réconcilie le compte personnel de l'invité à `OnPlayerReady`, sans attendre F7. Le portefeuille natif hôte conserve son chemin BDVM existant. Un solde individuel nul existant n'est pas réinitialisé au capital de départ.
- `PlayerWallet` sélectionne le portefeuille natif uniquement pour l'hôte ; les invités utilisent leur ledger autoritaire. Devis/achats boutique, suppression/réraillage, train de service, licences, voyage rapide et leurs remboursements passent par ce choix. Aucun repli vers les fonds hôte si le compte invité est absent ou insuffisant.
- Les dépôts des caisses à modules hors boutique appartiennent au déposant : autres joueurs refusés tant que le dépôt existe, annulation remboursée au propriétaire, reçu stable si la remise à zéro native échoue. Les remboursements peuvent terminer après déconnexion, mais pas entrer dans une nouvelle autorité de portefeuille. Le propriétaire de dépôt est un état physique de session, pas une nouvelle racine économique persistée.
- Connexion simultanée avec une identité persistante déjà présente refusée ; le garde des comptes refuse aussi une identité ambiguë. Le protocole passe de 4 à 5 car le paquet monétaire change. Mettre à jour hôte et invités ensemble.

## Frontières et coût

Les entrées restent les callbacks réseau/jeu existants sur Unity. Aucun polling, worker, scan du monde ou nouvelle file ajouté. Recherche du destinataire dans les joueurs connectés uniquement lors d'une mutation de portefeuille ; index du ledger et reçus existants conservés. Les confirmations et sauvegardes suivent leur chemin actuel. Aucun gain de frame time mesuré.

## Preuves et qualification restante

`tools/Validate.ps1` réussit : 154 tests protocole, 11 contrôles Harmony boutique, suite backend boutique, 17 contrôles objets, 8 contrôles identité F7, 19 contrôles matériel de compagnie et **24 contrôles d'isolation de portefeuille**. Le nouveau banc utilise le routage serveur, l'API, le ledger, la réception client et le remboursement de caisse de production avec surfaces natives simulées, ainsi que la sérialisation LiteNetLib réelle. Cas : hôte + deux invités, bouton hôte sans diffusion, achat/refus/remboursement, transfert, save/reload, reconnexion, identité ambiguë, zéro persistant, vieille session, chargement, précision double et destinataire étranger.

Compilations Multiplayer et BDVM.Full réussies. Non déployé. Tester dans Unity : deux invités aux soldes distincts, crédit magique hôte, F7 et portefeuille physique, boutique/annulation, caisse de service, voyage rapide, reconnexion et sauvegarde/rechargement. Les frais/récompenses natifs qui produisent des objets physiques restent à qualifier dans ces parcours réels. Les soldes individuels déjà enregistrés sont conservés ; aucune répartition arbitraire du solde commun antérieurement affiché n'est effectuée.
