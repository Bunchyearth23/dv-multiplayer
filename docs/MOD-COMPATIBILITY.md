# Compatibilité MultiplayerAPI et BDVM

État audité le 8 septembre 2026. Cette matrice décrit les contrats observables du fork ; elle ne vaut pas qualification en jeu.

## Contrat d'admission

Les mods classés `All`, `Undefined` ou sans déclaration sont requis des deux côtés. L'identifiant est comparé sans tenir compte de la casse et la version est comparée exactement. Une liste absente, une identité dupliquée, un identifiant vide ou une version vide est refusé. Les mods `Host` et `Client` ne participent pas à l'ensemble requis ; ils doivent eux-mêmes désactiver leur comportement lorsqu'ils s'exécutent du mauvais côté.

Les callbacks lifecycle et tick de `MultiplayerAPI` sont isolés : l'exception d'un adapter est journalisée et n'empêche pas les autres adapters ni le cœur de la session de poursuivre. Cette isolation ne peut pas annuler les effets partiels déjà produits par l'adapter fautif.

Le wire protocol courant est `dvmp-protocol:4`. Il n'est pas compatible avec les builds 1–3 : la présence explicite de l'état ouvert/fermé de chaque main a été ajoutée afin qu'un delta sans état de main ne rouvre pas une main distante.

## Matrice

| Classe / intégration | Host | Client | Version | Absence ou erreur | Qualification restante |
| --- | --- | --- | --- | --- | --- |
| Multiplayer | requis | requis | exacte | connexion refusée | host/client réel |
| Adapter BDVM déclaré `All` | requis | requis | exacte | connexion refusée | installer le même adapter sur deux instances |
| Adapter BDVM déclaré `Host` | requis sur host seulement | facultatif | non négociée | doit se neutraliser côté client | ordre de chargement et sauvegarde |
| Adapter BDVM déclaré `Client` | facultatif | facultatif | non négociée | aucun état autoritaire permis | UI/contrôles localement |
| Adapter absent ou sans déclaration | requis par défaut | requis | exacte | refus sûr | déclaration explicite avant support officiel |
| Adapter en erreur dans un callback API | selon classe | selon classe | — | callback isolé | logs et absence d'état Unity partiel |
| Paint themes / custom tasks | dépend de l'adapter | dépend de l'adapter | API `1.2.0.0` | refus hors session ; IDs non persistants | late join, sauvegarde et conflits |

## Limites connues

Il n'existe pas encore de négociation fine de capacités, de plage semver, ni de contrat générique de sauvegarde pour les données propres aux adapters. La compatibilité exacte protège la session mais oblige à aligner les versions. Un adapter doit documenter son ordre de chargement, utiliser une identité persistante distincte des NetId de session et rendre ses opérations idempotentes.

Avant d'annoncer un adapter supporté, tester : adapter absent, version différente, ordre inversé, exception au démarrage/tick/arrêt, late join, reconnexion et reload. Les adapters BDVM réels et leurs `info.json` sont indispensables pour compléter une liste nominative ; ils ne sont pas présents dans ce repository.
