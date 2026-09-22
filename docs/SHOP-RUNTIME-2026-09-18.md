# Magasin en jeu : portefeuille et création des articles — 18 septembre 2026

Signalement utilisateur : partie solo, magasin physique, portefeuille qui ne fonctionne pas.
Le Player.log local du 17 septembre contient douze refus `Shop purchase rejected: ServerError, line -1.` entre 19:54:59.821 et 19:55:02.285 (lignes 5426–5437). Le parcours RPC Multiplayer intervient dans cette session. Cela confirme un échec d'achat, pas sa phase ni la cause du problème de portefeuille décrit.

Le patch `CashRegisterBase.AddCash` restituait l'argent inséré dans une caisse shop sans présenter de paiement accepté. Le backend doit débiter le portefeuille partagé au bouton Buy. Le ledger absorbait toutes les exceptions sans conserver leur diagnostic : les anciennes traces ne permettent pas d'attribuer individuellement chaque refus à une phase.

## Défauts corrigés

- `MoneyUse.HandleUse` (usage non-VR) et `CashRegisterBase.OnTriggerEnter` (portefeuille tenu, VR) sont interceptés avant `Wallet.TrySpend`. Présenter le portefeuille demande un devis serveur en lecture seule. Son total et le texte localisé « prêt à acheter » sont affichés. `DepositedCash` reste intact : aucune somme fictive ne peut être remboursée par Cancel, unload ou distance. Le bouton Buy conserve la transaction autoritaire idempotente, avec nouvelle vérification des fonds, prix et stocks. Les billets et caisses de service gardent leur chemin existant ; après conversion d'un billet en solde, le shop demande aussi un devis.
- Le devis local est invalidé au changement du panier, à l'annulation, à l'achat et à la désactivation/destruction de la caisse. Générations et état terminal rejettent timeout/réponse périmée ou dupliquée. Les anciens packets de caisse ne modifient plus le panier d'un autre joueur dans un shop. Aucune réservation de stock ou de fonds n'est créée par le devis.
- `ShopPurchaseBackend.Prepare` exigeait `ItemBase` sur le prefab. L'inspection des assemblies du jeu installé établit que `ControlSpec.Awake` appelle `ControlsInstantiator.Spawn`, qui ajoute `ItemNonVR` ou `ItemVRTK` à partir de `DV.CabControls.Spec.Item`. Le prefab peut donc légitimement ne pas encore contenir `ItemBase`. La préparation vérifie désormais la spécification native (ou un ItemBase déjà présent pour un prefab personnalisé), puis le commit vérifie l'initialisation effective après activation. Les shops sans référence valide sont ignorés lors de la résolution. Le rollback conserve le remboursement unique, l'annulation du stock et la destruction sans double restock.
- L'exception et la phase de transaction sont conservées dans le résultat local ; les deux exceptions sont préservées si la compensation échoue. Le serveur journalise opération, caisse, articles, phase et exception. Aucun détail d'exception n'est ajouté au protocole réseau.

Tous les accès et effets Unity restent sur leur thread actuel ; aucun nouveau scan périodique, worker ou journal persistant n'est introduit. Le devis est uniquement une présentation, jamais l'autorité d'un achat.

## Preuves hors jeu

- `tools/Validate.ps1` : conformité de distribution, builds et 154 tests de protocole réussis ; 11 contrôles des vrais patches Harmony sur surfaces natives simulées réussis.
- `ShopBackend.Tests` compile le vrai backend et le ledger : prefab avec spécification mais sans ItemBase initial, activation différée, débit/livraison uniques, replay, second acheteur sur stock épuisé, shop nul, erreurs d'initialisation et de stockage avec compensation, prefab invalide sans débit.
- Le même scénario exécuté contre `ShopPurchaseBackend.cs` de HEAD avant correction échoue dans Prepare avec `Shop item prefab is missing required components: lamp`. Il réussit après correction. Les fixtures ne sont pas une exécution du moteur Unity.
- Les diagnostics locaux et l'inspection du jeu sont dans `../dv-company/artifacts/shop-diagnostics/` depuis la racine du dépôt ; le code décompilé du jeu n'est pas distribué avec le mod.

## Qualification en jeu restante

Le candidat doit être utilisé côté hôte et clients : scanner un article, présenter le portefeuille et acheter. Vérifier montant/feedback, débit unique, objet visible par tous et reçu ; refaire avec plusieurs articles, fonds insuffisants, dernier article acheté simultanément, annulation et changement du panier pendant le devis, timeout/retry, unload et save/reload. Tester l'usage normal et VR. Aucun test Unity effectué pendant cette investigation ; aucun gain de performance annoncé.

## Installation autorisée et vérifiée

`unity-candidate-20260918-shop-wallet-r1` reconstruit puis installé le 18 septembre, jeu fermé. `Test-UnityCandidate.ps1` : 96 fichiers préparés, 25 groupes verts ; les deux suites shop supplémentaires sont également vertes en Release (logs `shop-wallet-extra.log` et `shop-backend-extra.log` dans les preuves du candidat). Dix bibliothèques remplacées, 85 fichiers déjà identiques. La configuration séparée n'est pas appliquée : comparaison officielle finale 95/95 SAME et empreinte de `runtime-settings.json` inchangée (`628C303FF69F31B09CE284F5A8E828E93DDD867A09D26DAA436CFA578D1420D5`).

Sauvegarde vérifiée des 95 fichiers précédents : `dv-company/artifacts/unity-candidates/unity-candidate-20260918-shop-wallet-r1/backups/activation-20260918-085609`, manifeste de déploiement `complete`, `configurationApplied=false`. Aucune partie lancée et aucune installation effectuée sur une autre machine.
