// The API's types, made by `npm run api` from the OpenAPI document in
// tests/GalaxyData.Web.Tests/Hosting/Snapshots. Don't edit them: change the API, accept its document, and run it again.

export interface paths {
    "/api/health": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Whether the application can do its work
         * @description 200 when it can; 503, with the checks that failed, when it can't. Anyone may ask.
         */
        get: operations["GetHealth"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/session": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** Who is signed in; gives the anti-forgery token (the XSRF-TOKEN cookie) */
        get: operations["GetSession"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/sign-in": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Signs in, starting a session (the gd.auth cookie) */
        post: operations["SignIn"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/sign-out": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Ends the session */
        post: operations["SignOut"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/change-password": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Changes the signed-in user's password; their other sessions end */
        post: operations["ChangePassword"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/password-policy": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** What a password must be, for forms to say before it is sent */
        get: operations["GetPasswordPolicy"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/users": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListUsers"];
        put?: never;
        post: operations["CreateUser"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/users/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetUser"];
        put: operations["UpdateUser"];
        post?: never;
        delete: operations["DeleteUser"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/users/{id}/reset-password": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Sets a user's password, which they must change at their next sign-in; their sessions end */
        post: operations["ResetUserPassword"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/users/{id}/unlock": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Lets a user locked out by failed sign-ins sign in again */
        post: operations["UnlockUser"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/audit/admin-events": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** What administrators did, newest first; the next page is before the last id given */
        get: operations["ListAdminEvents"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/audit/commits": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** Commits of changes, newest first; the next page is before the last id given */
        get: operations["ListCommits"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/audit/commits/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** A commit of changes, with the scripts it ran */
        get: operations["GetCommit"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connection-kinds": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The kinds of connection, and their forms */
        get: operations["ListConnectionKinds"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connection-kinds/{kind}/convert": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** The same settings as a connection string, or as the form's; secrets come back only as given */
        post: operations["ConvertConnection"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connections": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListConnections"];
        put?: never;
        post: operations["CreateConnection"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connections/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetConnection"];
        put: operations["UpdateConnection"];
        post?: never;
        delete: operations["DeleteConnection"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connections/{id}/test": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Tries a connection as it is saved */
        post: operations["TestConnection"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connections/test": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Tries settings before they are saved */
        post: operations["TestConnectionSettings"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connections/{id}/refresh": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Reads the connection's schema again, in the background; its schemaStatus says how it goes */
        post: operations["RefreshConnectionSchema"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connections/{id}/snapshots": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The schemas kept of the connection, newest first, and how much changed in each */
        get: operations["ListSchemaSnapshots"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/connections/{id}/snapshots/{snapshotId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** A schema kept of the connection, and what changed since the one before it */
        get: operations["GetSchemaSnapshot"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/catalog": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The catalog's version and sources */
        get: operations["GetCatalog"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/catalog/tree/children": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The children of a node of the tree, or its top nodes (the sources) */
        get: operations["GetTreeChildren"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/catalog/tree/search": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** Nodes whose names (or paths, or columns) have the text in them */
        get: operations["SearchTree"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/catalog/entity": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** An entity, by its name as queries write it */
        get: operations["GetEntity"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/catalog/paths": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The navigations that lead from one entity's rows to another's, shortest first */
        get: operations["FindCatalogPaths"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/browse/page": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** A page of rows to browse, with their columns and count when asked for */
        post: operations["BrowsePage"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/browse/trail": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** The crumbs of a trail as they stand: the entities they reach, and the rows chosen in them */
        post: operations["BrowseTrail"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/browse/position": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Where a row is among its entity's rows: its key, and how many rows come before it in the key's order */
        post: operations["BrowsePosition"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** Every item of the overlay, with what the catalog finds wrong with it */
        get: operations["GetOverlay"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/export": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The overlay as a JSON file, as gdq reads it (--overlay) */
        get: operations["ExportOverlay"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/relations/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetRelation"];
        put: operations["UpdateRelation"];
        post?: never;
        delete: operations["DeleteRelation"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/relations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CreateRelation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/relations/validate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Tries a relation, in place of the relation id when given, without saving it */
        post: operations["ValidateRelation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/navigations/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetNavigationOverride"];
        put: operations["UpdateNavigationOverride"];
        post?: never;
        delete: operations["DeleteNavigationOverride"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/navigations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CreateNavigationOverride"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/navigations/validate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Tries a navigation override, in place of the override id when given, without saving it */
        post: operations["ValidateNavigationOverride"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/virtual-entities/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetVirtualEntity"];
        put: operations["UpdateVirtualEntity"];
        post?: never;
        delete: operations["DeleteVirtualEntity"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/virtual-entities": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CreateVirtualEntity"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/virtual-entities/validate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Tries a virtual entity, in place of the virtual entity id when given, without saving it: its issues, the entity it makes, and its query's diagnostics */
        post: operations["ValidateVirtualEntity"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/entity-settings/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetEntitySettings"];
        put: operations["UpdateEntitySettings"];
        post?: never;
        delete: operations["DeleteEntitySettings"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/entity-settings": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CreateEntitySettings"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/overlay/entity-settings/validate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Tries an entity's settings, in place of the settings id when given, without saving them: their issues, and the entity as they make it */
        post: operations["ValidateEntitySettings"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/query/validate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** What is wrong with a query, placed in its text; the parameters it uses (those without values taken as null), and its columns */
        post: operations["ValidateQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/query/explain": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** How a query would run, without running it; with a grid, the page the grid would fetch */
        post: operations["ExplainQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/query/execute": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** A page of a query's rows, as a grid shows them, with their columns and count when asked for */
        post: operations["ExecuteQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/query/link": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Where a value of a row leads: the query of those rows, and what a grid can browse for them */
        post: operations["FollowQueryLink"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/saved-queries": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The user's saved queries, and those shared, by name */
        get: operations["ListSavedQueries"];
        put?: never;
        post: operations["CreateSavedQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/saved-queries/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetSavedQuery"];
        put: operations["UpdateSavedQuery"];
        post?: never;
        delete: operations["DeleteSavedQuery"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/changes": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The user's pending changes, in the order they were first made */
        get: operations["GetChanges"];
        put?: never;
        post?: never;
        /** Drops the pending changes, or those of a source or an entity */
        delete: operations["ClearChanges"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/changes/ops": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Applies operations to the pending changes: all of them, or none */
        post: operations["ApplyChangeOps"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/changes/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** The statements committing the changes would run on each connection, and why changes can't be made; a plan to commit when none */
        post: operations["PreviewChanges"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/changes/commit": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Commits a preview's plan, with scripts as edited; what came of it on each connection */
        post: operations["CommitChanges"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The user's dashboards, and those shared with them, by name */
        get: operations["ListDashboards"];
        put?: never;
        post: operations["CreateDashboard"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/people": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** Users to share dashboards with, found by name */
        get: operations["FindPeople"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetDashboard"];
        /** Saves the working copy */
        put: operations["UpdateDashboard"];
        post?: never;
        delete: operations["DeleteDashboard"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/palettes": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The palettes the copies the user sees name, as they are now */
        get: operations["GetDashboardPalettes"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/publish": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Publishes the working copy as the next revision */
        post: operations["PublishDashboard"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/discard": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Puts the published copy back as the working copy */
        post: operations["DiscardDashboardChanges"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/revisions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListDashboardRevisions"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/revisions/{number}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetDashboardRevision"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/revisions/{number}/restore": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Copies a revision into the working copy */
        post: operations["RestoreDashboardRevision"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/sharing": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: operations["ShareDashboard"];
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/public": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        /** Makes the dashboard public, or not, and says who may frame it */
        put: operations["SetDashboardPublic"];
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/public/regenerate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Gives the public link a new token; the old one stops */
        post: operations["RegenerateDashboardLink"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/copy": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Saves a copy as a new dashboard of the user's */
        post: operations["CopyDashboard"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/data": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** A widget's rows, of a definition sent */
        post: operations["GetSliceData"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** A widget's queries, of a definition sent */
        post: operations["GetSliceQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/filter-values": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** A filter's values, of a definition sent */
        post: operations["GetSliceFilterValues"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/widgets/{widget}/data": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** A widget's rows, of the published copy */
        post: operations["GetWidgetData"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/widgets/{widget}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** A widget's queries, of the published copy */
        post: operations["GetWidgetQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/dashboards/{id}/filters/{filter}/values": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** A filter's values, of the published copy */
        post: operations["GetFilterValues"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/palettes": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** Every palette, by name */
        get: operations["ListPalettes"];
        put?: never;
        post: operations["CreatePalette"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/palettes/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetPalette"];
        put: operations["UpdatePalette"];
        post?: never;
        delete: operations["DeletePalette"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/public/dashboards/{token}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** A public dashboard: what it shows, without its queries */
        get: operations["GetPublicDashboard"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/public/dashboards/{token}/widgets/{widget}/data": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** A public dashboard's widget's rows, for the state given (s) */
        get: operations["GetPublicWidgetData"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/public/dashboards/{token}/filters/{filter}/values": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** The values a public dashboard's filter may take */
        get: operations["GetPublicFilterValues"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        AdminEventDto: {
            /** Format: int64 */
            id: number;
            /** Format: date-time */
            at: string;
            actor: string;
            action: string;
            target: string;
            details: null | string;
        };
        /** @enum {unknown} */
        Aggregate: "count" | "countValues" | "countDistinct" | "sum" | "avg" | "min" | "max";
        /**
         * @default measure
         * @enum {unknown}
         */
        BarColorBy: "measure" | "category";
        /** @enum {unknown} */
        BarOrientation: "vertical" | "horizontal";
        /** @enum {unknown} */
        BarStack: "none" | "stacked" | "percent";
        Breakpoint: {
            id: string;
            label: string;
            /** Format: int32 */
            minWidth: number;
            /** Format: int32 */
            columns: number;
        };
        BrowseFromDto: {
            entity: string;
            key: unknown[];
        };
        BrowsePageDto: {
            queryText: string;
            parameters: components["schemas"]["QueryParameterDto"][];
            entity: string;
            schema: null | components["schemas"]["BrowseSchemaDto"];
            rows: components["schemas"]["GridRowDto"][];
            /** Format: int64 */
            offset: number;
            hasMore: boolean;
            /** Format: int64 */
            total: null | number;
        };
        BrowsePageRequest: {
            source: components["schemas"]["BrowseSourceDto"];
            grid?: null | components["schemas"]["GridStateDto"];
            /** @default true */
            includeSchema: boolean;
            /** @default false */
            includeCount: boolean;
        };
        BrowsePositionDto: {
            id: null | string;
            /** Format: int64 */
            index: null | number;
        };
        BrowsePositionRequest: {
            entity: string;
            columns: string[];
            values: unknown[];
        };
        BrowseSchemaDto: {
            entity: string;
            key: null | string[];
            capabilities: components["schemas"]["CapabilitiesDto"];
            columns: components["schemas"]["GridColumnDto"][];
            references: components["schemas"]["GridReferenceDto"][];
            collections: components["schemas"]["GridCollectionDto"][];
        };
        BrowseSourceDto: {
            entity?: null | string;
            from?: null | components["schemas"]["BrowseFromDto"];
            navigation?: null | string;
        };
        BrowseTrailDto: {
            crumbs: components["schemas"]["TrailStepDto"][];
        };
        BrowseTrailRequest: {
            crumbs: components["schemas"]["TrailCrumbDto"][];
        };
        /** @enum {unknown} */
        Bucket: "day" | "week" | "month" | "quarter" | "year" | "quarterOfYear" | "monthOfYear" | "dayOfWeek" | "hourOfDay";
        CapabilitiesDto: {
            canInsert: boolean;
            canUpdate: boolean;
            canDelete: boolean;
            insertReason?: null | string;
            changeReason?: null | string;
        };
        CatalogDiagnosticDto: {
            code: string;
            severity: components["schemas"]["DiagnosticSeverity"];
            message: string;
            subject: null | string;
            item: null | components["schemas"]["OverlayItemDto"];
        };
        CatalogDto: {
            version: string;
            sources: components["schemas"]["CatalogSourceDto"][];
            diagnostics: components["schemas"]["CatalogDiagnosticDto"][];
        };
        CatalogOverlay: {
            relations?: components["schemas"]["OverlayRelation"][];
            virtualEntities?: components["schemas"]["OverlayVirtualEntity"][];
            entities?: components["schemas"]["OverlayEntitySettings"][];
            navigations?: components["schemas"]["OverlayNavigation"][];
            naming?: null | components["schemas"]["NavigationNamingOptions"];
        };
        CatalogPathDto: {
            steps: components["schemas"]["CatalogPathStepDto"][];
        };
        CatalogPathStepDto: {
            navigation: string;
            from: string;
            to: string;
            isCollection: boolean;
        };
        CatalogSourceDto: {
            alias: string;
            kind: string;
            displayName: null | string;
            status: components["schemas"]["SchemaStatus"];
            /** Format: date-time */
            refreshedAt: null | string;
            isReadOnly: boolean;
            hasSchema: boolean;
            /** Format: int32 */
            entities: number;
            problem: null | string;
        };
        ChangeCountsDto: {
            /** Format: int32 */
            added: number;
            /** Format: int32 */
            removed: number;
            /** Format: int32 */
            changed: number;
        };
        ChangeIssueDto: {
            /** Format: int64 */
            change: number;
            column: null | string;
            message: string;
        };
        ChangeOpDto: {
            op: null | components["schemas"]["ChangeOpKind"];
            entity?: null | string;
            key?: null | unknown[];
            tempId?: null | string;
            /** Format: int64 */
            change?: null | number;
            values?: null | Record<string, unknown>;
            original?: null | Record<string, unknown>;
            display?: null | Record<string, unknown>;
            columns?: null | string[];
        };
        /** @enum {unknown} */
        ChangeOpKind: "set" | "insert" | "delete" | "revert";
        ChangeOpsRequest: {
            ops: components["schemas"]["ChangeOpDto"][];
            /** Format: int32 */
            version?: null | number;
        };
        ChangePasswordRequest: {
            currentPassword: string;
            newPassword: string;
        };
        ChangePreviewDto: {
            planId: null | string;
            /** Format: int32 */
            version: number;
            catalogVersion: string;
            /** Format: date-time */
            expiresAt: null | string;
            multiConnection: boolean;
            scripts: components["schemas"]["PreviewScriptDto"][];
            issues: components["schemas"]["ChangeIssueDto"][];
        };
        ChangeSetDto: {
            /** Format: int32 */
            version: number;
            /** Format: date-time */
            updatedAt: null | string;
            changes: components["schemas"]["PendingChangeDto"][];
        };
        ChartPaletteDto: {
            /** Format: int32 */
            id: number;
            name: string;
            owner: string;
            definition: components["schemas"]["PaletteDefinition"];
            hash: string;
        };
        ChoiceDto: {
            value: string;
            label: string;
        };
        ColumnLinkDto: {
            kind: components["schemas"]["LinkKind"];
            ordinals: number[];
            target: null | string;
            navigation: null | string;
            multiplicity: null | components["schemas"]["Multiplicity"];
        };
        /** @enum {unknown} */
        ColumnRole: "dimension" | "series" | "measure" | "key" | "column";
        ColumnSchema: {
            name: string;
            /** Format: int32 */
            ordinal: number;
            nativeType: string;
            type: components["schemas"]["ScalarType"];
            isIdentity?: boolean;
            isComputed?: boolean;
            defaultSql?: null | string;
            isRowVersion?: boolean;
            collation?: null | string;
            readAs?: null | string;
            comment?: null | string;
        };
        ColumnSettingDto: {
            name: string;
            /** @default false */
            hidden: boolean;
            label?: null | string;
            type?: null | string;
        };
        CommitAuditDto: {
            /** Format: int64 */
            id: number;
            /** Format: date-time */
            startedAt: string;
            /** Format: date-time */
            finishedAt: null | string;
            user: string;
            status: components["schemas"]["CommitStatus"];
            /** Format: int32 */
            changes: number;
            edited: boolean;
            anyStatement: boolean;
            catalogVersion: string;
            failureKind: null | string;
            failure: null | string;
            scripts: components["schemas"]["CommitAuditScriptDto"][];
        };
        CommitAuditScriptDto: {
            source: string;
            kind: string;
            dialect: string;
            edited: boolean;
            status: components["schemas"]["CommitScriptStatus"];
            /** Format: int32 */
            statements: number;
            /** Format: int64 */
            rowsChanged: null | number;
            error: null | string;
            text: string;
        };
        CommitAuditSummaryDto: {
            /** Format: int64 */
            id: number;
            /** Format: date-time */
            startedAt: string;
            /** Format: date-time */
            finishedAt: null | string;
            user: string;
            status: components["schemas"]["CommitStatus"];
            /** Format: int32 */
            changes: number;
            sources: string[];
            edited: boolean;
            anyStatement: boolean;
            failure: null | string;
        };
        CommitChangesRequest: {
            planId: string;
            /** Format: int32 */
            version: number;
            scripts?: null | components["schemas"]["EditedScriptDto"][];
            /** @default false */
            allowAnyStatement: boolean;
        };
        CommitFailureDto: {
            kind: components["schemas"]["DmlFailureKind"];
            source: string;
            message: string;
            /** Format: int64 */
            change: null | number;
            statement: null | string;
        };
        CommitResultDto: {
            /** Format: int64 */
            auditId: number;
            outcome: components["schemas"]["DmlOutcome"];
            scripts: components["schemas"]["CommitScriptDto"][];
            failure: null | components["schemas"]["CommitFailureDto"];
            inserted: components["schemas"]["InsertedRowDto"][];
            changes: components["schemas"]["ChangeSetDto"];
            warnings: string[];
        };
        CommitScriptDto: {
            source: string;
            edited: boolean;
            status: components["schemas"]["DmlScriptStatus"];
            error: null | string;
            statements: components["schemas"]["CommitStatementDto"][];
        };
        /** @enum {unknown} */
        CommitScriptStatus: "pending" | "committed" | "rolledBack" | "commitFailed" | "unknown";
        CommitStatementDto: {
            /** Format: int64 */
            change: null | number;
            description: string;
            /** Format: int64 */
            rowsChanged: number;
        };
        /** @enum {unknown} */
        CommitStatus: "inProgress" | "committed" | "rolledBack" | "partiallyCommitted" | "unknown";
        /** @enum {unknown} */
        ConditionOp: "eq" | "ne" | "lt" | "le" | "gt" | "ge" | "between" | "contains" | "notContains" | "startsWith" | "endsWith" | "blank" | "notBlank" | "in" | "notIn" | "relative";
        ConditionValue: {
            op: components["schemas"]["ConditionOp"];
            value?: unknown;
            valueTo?: unknown;
            values?: null | unknown[];
            relative?: null | components["schemas"]["RelativeRange"];
        };
        ConnectionDto: {
            /** Format: int32 */
            id: number;
            alias: string;
            kind: string;
            displayName: null | string;
            mode: components["schemas"]["ConnectionMode"];
            settings: {
                [key: string]: string;
            };
            secrets: {
                [key: string]: components["schemas"]["SecretStateDto"];
            };
            connectionString: null | string;
            options: {
                [key: string]: string;
            };
            isReadOnly: boolean;
            secretsUnreadable: boolean;
            schemaStatus: components["schemas"]["SchemaStatus"];
            schemaError: null | string;
            /** Format: date-time */
            schemaRefreshedAt: null | string;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        ConnectionInput: {
            mode?: components["schemas"]["ConnectionMode"];
            settings?: null | {
                [key: string]: string;
            };
            secrets?: null | {
                [key: string]: components["schemas"]["SecretInput"];
            };
            connectionString?: null | string;
            options?: null | {
                [key: string]: string;
            };
            /** @default true */
            isReadOnly: boolean;
        };
        ConnectionKindDto: {
            id: string;
            displayName: string;
            supportsRaw: boolean;
            alwaysReadOnly: boolean;
            rawExample: null | string;
            groups: components["schemas"]["GroupDto"][];
            fields: components["schemas"]["FieldDto"][];
            options: components["schemas"]["FieldDto"][];
        };
        /**
         * @default form
         * @enum {unknown}
         */
        ConnectionMode: "form" | "raw";
        ConnectionTestDto: {
            ok: boolean;
            message: string;
            /** Format: double */
            elapsedMs: number;
        };
        ConvertConnectionRequest: {
            connection: components["schemas"]["ConnectionInput"];
            to: components["schemas"]["ConnectionMode"];
        };
        CopyDashboardRequest: {
            name: string;
        };
        CreateConnectionRequest: {
            alias: string;
            kind: string;
            connection: components["schemas"]["ConnectionInput"];
            displayName?: null | string;
        };
        CreateDashboardRequest: {
            name: string;
            description?: null | string;
            definition?: null | components["schemas"]["DashboardDefinition"];
        };
        CreatePaletteRequest: {
            name: string;
            description: null | string;
            definition: components["schemas"]["PaletteDefinition"];
        };
        CreateUserRequest: {
            userName: string;
            role: components["schemas"]["UserRole"];
            password: string;
            displayName?: null | string;
        };
        DashboardDefinition: {
            /** Format: int32 */
            schema: number;
            layout: components["schemas"]["DashboardLayout"];
            sources: components["schemas"]["DashboardSource"][];
            links: components["schemas"]["DashboardLink"][];
            filters: components["schemas"]["DashboardFilter"][];
            widgets: components["schemas"]["DashboardWidget"][];
            refresh: components["schemas"]["RefreshPolicy"];
            public: components["schemas"]["PublicSettings"];
            /** Format: int32 */
            palette?: null | number;
        };
        DashboardDto: {
            /** Format: int32 */
            id: number;
            name: string;
            description: null | string;
            owner: string;
            isMine: boolean;
            working: null | components["schemas"]["DashboardDefinition"];
            workingHash: null | string;
            published: null | components["schemas"]["DashboardDefinition"];
            publishedHash: null | string;
            /** Format: int32 */
            publishedNumber: null | number;
            /** Format: date-time */
            publishedAt: null | string;
            publishedBy: null | string;
            hasUnpublishedChanges: boolean;
            sharing: null | components["schemas"]["DashboardSharingDto"];
            public: null | components["schemas"]["DashboardPublicDto"];
            can: components["schemas"]["DashboardPermissionsDto"];
            issues: components["schemas"]["DashboardIssue"][];
            palettes: components["schemas"]["ChartPaletteDto"][];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        DashboardFilter: {
            id: string;
            label: string;
            field: components["schemas"]["SourceField"];
            kind: components["schemas"]["FilterKind"];
            value: null | components["schemas"]["ConditionValue"];
            visible: boolean;
            editable: boolean;
            multiple: boolean;
            except: string[];
        };
        DashboardIssue: {
            severity: components["schemas"]["IssueSeverity"];
            message: string;
            widget: null | string;
            field: null | string;
        };
        DashboardLayout: {
            /** Format: int32 */
            rowHeight: number;
            /** Format: int32 */
            gap: number;
            breakpoints: components["schemas"]["Breakpoint"][];
            items: {
                [key: string]: components["schemas"]["Placement"];
            };
            overrides: {
                [key: string]: components["schemas"]["LayoutOverride"];
            };
        };
        DashboardLink: {
            from: string;
            to: string;
            path: string[];
        };
        DashboardPermissionsDto: {
            edit: boolean;
            publish: boolean;
            share: boolean;
            makePublic: boolean;
            revokePublic: boolean;
            delete: boolean;
            copy: boolean;
        };
        DashboardPublicDto: {
            token: string;
            origins: string[];
            enabledBy: string;
            /** Format: date-time */
            enabledAt: string;
            works: boolean;
        };
        DashboardPublicRequest: {
            enabled: boolean;
            origins: null | string[];
            /** Format: int32 */
            version: number;
        };
        DashboardRevisionDto: {
            /** Format: int32 */
            number: number;
            /** Format: date-time */
            publishedAt: string;
            publishedBy: string;
            note: null | string;
            isPublished: boolean;
            definition: components["schemas"]["DashboardDefinition"];
        };
        DashboardRevisionSummaryDto: {
            /** Format: int32 */
            number: number;
            /** Format: date-time */
            publishedAt: string;
            publishedBy: string;
            note: null | string;
            isPublished: boolean;
        };
        /** @enum {unknown} */
        DashboardSharing: "private" | "chosen" | "everyone";
        DashboardSharingDto: {
            everyone: boolean;
            users: components["schemas"]["PersonDto"][];
        };
        DashboardSharingRequest: {
            everyone: boolean;
            users: null | number[];
            /** Format: int32 */
            version: number;
        };
        DashboardSource: {
            id: string;
            entity: string;
            label: string;
        };
        DashboardState: {
            filters?: null | {
                [key: string]: components["schemas"]["ConditionValue"];
            };
            selections?: null | {
                [key: string]: components["schemas"]["SelectionState"];
            };
        };
        DashboardSummaryDto: {
            /** Format: int32 */
            id: number;
            name: string;
            description: null | string;
            owner: string;
            isMine: boolean;
            sharing: components["schemas"]["DashboardSharing"];
            isPublic: boolean;
            /** Format: int32 */
            publishedNumber: null | number;
            /** Format: date-time */
            publishedAt: null | string;
            hasUnpublishedChanges: boolean;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        DashboardVersionRequest: {
            /** Format: int32 */
            version: number;
        };
        DashboardWidget: {
            id: string;
            title: null | string;
            config: components["schemas"]["WidgetConfig"];
        };
        DiagnosticDto: {
            code: string;
            severity: components["schemas"]["DiagnosticSeverity"];
            message: string;
            /** Format: int32 */
            start: number;
            /** Format: int32 */
            end: number;
        };
        /** @enum {unknown} */
        DiagnosticSeverity: "info" | "warning" | "error";
        Dimension: {
            field: components["schemas"]["FieldRef"];
            bucket: null | components["schemas"]["Bucket"];
            label: string;
        };
        /** @enum {unknown} */
        DmlFailureKind: "connection" | "statement" | "conflict" | "commit" | "timeout";
        /** @enum {unknown} */
        DmlOutcome: "committed" | "rolledBack" | "partiallyCommitted";
        /** @enum {unknown} */
        DmlScriptStatus: "committed" | "rolledBack" | "commitFailed";
        /** @enum {unknown} */
        DmlStatementKind: "insert" | "update" | "delete" | "merge" | "other";
        EditedScriptDto: {
            source: string;
            text: string;
        };
        EditTargetDto: {
            entity: string;
            column: string;
            keyOrdinals: number[];
            canUpdate: boolean;
            readOnlyReason: null | string;
        };
        EntityColumnDto: {
            name: string;
            /** Format: int32 */
            ordinal: number;
            type: components["schemas"]["TypeDto"];
            nativeType: null | string;
            isKey: boolean;
            isIdentity: boolean;
            isComputed: boolean;
            hasDefault: boolean;
            isRowVersion: boolean;
            hidden: boolean;
            label: null | string;
            comment: null | string;
            canUpdate: boolean;
            insert: components["schemas"]["InsertMode"];
            readOnlyReason: null | string;
        };
        EntityDto: {
            name: string;
            qualifiedName: string;
            kind: components["schemas"]["EntityKind"];
            source: null | string;
            schema: null | string;
            table: null | string;
            comment: null | string;
            /** Format: int64 */
            rowCountEstimate: null | number;
            hasTriggers: boolean;
            key: null | components["schemas"]["KeyDto"];
            uniqueKeys: components["schemas"]["KeyDto"][];
            displayColumn: null | string;
            columns: components["schemas"]["EntityColumnDto"][];
            navigations: components["schemas"]["NavigationDto"][];
            capabilities: components["schemas"]["CapabilitiesDto"];
            query: null | string;
            problem: null | string;
            overlay: null | components["schemas"]["EntityOverlayDto"];
        };
        /** @enum {unknown} */
        EntityKind: "table" | "view" | "virtual";
        EntityOverlayDto: {
            /** Format: int32 */
            virtualEntity: null | number;
            /** Format: int32 */
            settings: null | number;
        };
        EntitySettingsDto: {
            /** Format: int32 */
            id: number;
            entity: string;
            key: null | string[];
            displayColumn: null | string;
            hidden: boolean;
            columns: components["schemas"]["ColumnSettingDto"][];
            issues: components["schemas"]["OverlayIssueDto"][];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        EntitySettingsInput: {
            entity: string;
            key?: null | string[];
            displayColumn?: null | string;
            /** @default false */
            hidden: boolean;
            columns?: null | components["schemas"]["ColumnSettingDto"][];
        };
        ExplainFragmentDto: {
            source: string;
            dialect: string;
            sql: string;
            parameters: components["schemas"]["ExplainParameter"][];
            strategy: string;
            table: null | string;
            /** Format: int64 */
            estimatedRows: null | number;
            bindJoinTemplate: null | string;
            bindJoinParameters: components["schemas"]["ExplainParameter"][];
        };
        ExplainNodeDto: {
            /** Format: int32 */
            id: number;
            operator: string;
            detail: null | string;
            site: null | string;
            columns: string[];
            /** Format: int64 */
            estimatedRows: null | number;
            inputs: number[];
            subqueries: components["schemas"]["ExplainSubqueryDto"][];
        };
        ExplainParameter: {
            name: string;
            type: string;
            value: string;
        };
        ExplainPhase: {
            name: string;
            rules: string[];
            plan: string;
        };
        ExplainSubqueryDto: {
            /** Format: int32 */
            number: number;
            kind: string;
            /** Format: int32 */
            plan: number;
        };
        /** @enum {unknown} */
        FetchStrategy: "full" | "keys" | "skipped" | "value";
        FieldDto: {
            key: string;
            label: string;
            type: components["schemas"]["FieldType"];
            required?: boolean;
            default?: null | string;
            placeholder?: null | string;
            help?: null | string;
            group?: null | string;
            choices?: null | components["schemas"]["ChoiceDto"][];
            visibleWhen?: null | components["schemas"]["VisibleWhenDto"];
            /** Format: int32 */
            min?: null | number;
            /** Format: int32 */
            max?: null | number;
        };
        FieldRef: {
            path: string[];
            column: string;
        };
        /** @enum {unknown} */
        FieldType: "text" | "number" | "password" | "bool" | "select" | "filePath" | "folderPath" | "keyValues";
        /** @enum {unknown} */
        FilterKind: "values" | "range" | "relative" | "text" | "boolean";
        FilterValueDto: {
            value: unknown;
            /** Format: int64 */
            rows: number;
        };
        FilterValuesDto: {
            type: components["schemas"]["TypeDto"];
            values: components["schemas"]["FilterValueDto"][];
            more: boolean;
        };
        ForeignKeySchema: {
            name: null | string;
            columns: string[];
            refSchema: string;
            refTable: string;
            refColumns: string[];
            isEnforced?: boolean;
            onDelete?: null | string;
            onUpdate?: null | string;
        };
        FragmentStatsDto: {
            source: string;
            table: string;
            /** Format: int64 */
            rows: number;
            /** Format: double */
            elapsedMs: number;
            strategy: components["schemas"]["FetchStrategy"];
            /** Format: int32 */
            keys: number;
            /** Format: int32 */
            batches: number;
        };
        GridCollectionDto: {
            navigation: string;
            target: string;
            multiplicity: components["schemas"]["Multiplicity"];
        };
        GridColumnDto: {
            name: string;
            type: components["schemas"]["TypeDto"];
            isKey: boolean;
            canUpdate: boolean;
            insert: components["schemas"]["InsertMode"];
            readOnlyReason: null | string;
            lineage: components["schemas"]["LineageDto"];
            /** Format: int32 */
            reference: null | number;
        };
        GridConditionDto: {
            op: components["schemas"]["GridOp"];
            value?: unknown;
            valueTo?: unknown;
        };
        GridFilterDto: {
            column: string;
            conditions: components["schemas"]["GridConditionDto"][];
            /** @default false */
            any: boolean;
        };
        /** @enum {unknown} */
        GridOp: "eq" | "ne" | "lt" | "le" | "gt" | "ge" | "between" | "contains" | "notContains" | "startsWith" | "endsWith" | "blank" | "notBlank";
        GridReferenceDto: {
            navigation: string;
            target: string;
            columns: number[];
            targetColumns: string[];
            complete: boolean;
            displayColumn: null | string;
            multiplicity: components["schemas"]["Multiplicity"];
        };
        GridRowDto: {
            id: null | string;
            k: null | unknown[];
            v: unknown[];
            r: null | unknown[];
        };
        GridSortDto: {
            column: string;
            /** @default false */
            desc: boolean;
        };
        GridStateDto: {
            filters?: null | components["schemas"]["GridFilterDto"][];
            where?: null | string;
            sort?: null | components["schemas"]["GridSortDto"][];
            /** Format: int64 */
            offset?: number;
            /** Format: int32 */
            limit?: null | number;
        };
        GroupDto: {
            key: string;
            label: string;
            collapsed: boolean;
        };
        HealthCheckDto: {
            name: string;
            status: components["schemas"]["HealthStatus"];
        };
        HealthResponse: {
            status: components["schemas"]["HealthStatus"];
            checks: components["schemas"]["HealthCheckDto"][];
        };
        /** @enum {unknown} */
        HealthStatus: "unhealthy" | "degraded" | "healthy";
        HttpValidationProblemDetails: {
            type?: null | string;
            title?: null | string;
            /** Format: int32 */
            status?: null | number;
            detail?: null | string;
            instance?: null | string;
            errors?: {
                [key: string]: string[];
            };
        };
        IndexSchema: {
            name: string;
            columns: string[];
            isUnique: boolean;
            isPrimaryKey?: boolean;
            filter?: null | string;
        };
        InlineFilterRequest: {
            slice: components["schemas"]["DashboardDefinition"];
            filter: string;
            state?: null | components["schemas"]["DashboardState"];
            search?: null | string;
        };
        InlineWidgetRequest: {
            slice: components["schemas"]["DashboardDefinition"];
            widget: string;
            state?: null | components["schemas"]["DashboardState"];
            page?: null | components["schemas"]["WidgetPage"];
            /** @default false */
            refresh: boolean;
        };
        InsertedRowDto: {
            tempId: string;
            entity: string;
            key: null | unknown[];
            rowId: null | string;
            values: Record<string, unknown>;
        };
        /** @enum {unknown} */
        InsertMode: "required" | "optional" | "never";
        /** @enum {unknown} */
        IssueSeverity: "error" | "warning";
        KeyDto: {
            name: null | string;
            columns: string[];
            isDeclared: boolean;
        };
        KeySchema: {
            name: null | string;
            columns: string[];
        };
        LabelColorDto: {
            label: null | string;
            light: string;
            dark: null | string;
        };
        LabelMatching: {
            ignoreCase: boolean;
            ignoreWhitespace: boolean;
            ignoreBrackets: boolean;
            ignoreAccents: boolean;
        };
        LayoutOverride: {
            /** Format: int32 */
            columns: number;
            items: {
                [key: string]: components["schemas"]["Placement"];
            };
            hidden: string[];
        };
        /** @enum {unknown} */
        LegendPosition: "none" | "top" | "bottom" | "left" | "right";
        LineageDto: {
            kind: components["schemas"]["LineageKind"];
            sources: components["schemas"]["LineageSourceDto"][];
            expression: null | string;
        };
        /** @enum {unknown} */
        LineageKind: "direct" | "computed" | "aggregated" | "constant" | "union" | "unknown";
        LineageSourceDto: {
            column: string;
            path: null | string;
        };
        /** @enum {unknown} */
        LineGaps: "zero" | "connect" | "break";
        /** @enum {unknown} */
        LinkKind: "row" | "collection" | "drillDown";
        /** @enum {unknown} */
        ListenMode: "all" | "none" | "chosen";
        Listens: {
            mode: components["schemas"]["ListenMode"];
            widgets: string[];
        };
        Measure: {
            aggregate: components["schemas"]["Aggregate"];
            field: null | components["schemas"]["FieldRef"];
            label: string;
            format: null | components["schemas"]["NumberFormat"];
        };
        /** @enum {unknown} */
        Multiplicity: "one" | "zeroOrOne" | "many";
        NavigationDto: {
            name: string;
            target: string;
            multiplicity: components["schemas"]["Multiplicity"];
            columns: string[];
            targetColumns: string[];
            isInverse: boolean;
            origin: components["schemas"]["RelationOrigin"];
            isEnforced: boolean;
            isCrossSource: boolean;
            hidden: boolean;
            inherited: boolean;
            overlay: null | components["schemas"]["NavigationOverlayDto"];
        };
        NavigationNamingOptions: {
            separatedSuffixes?: string[];
            camelSuffixes?: string[];
            displayColumnNames?: string[];
        };
        NavigationOverlayDto: {
            /** Format: int32 */
            relation: null | number;
            /** Format: int32 */
            override: null | number;
        };
        NavigationOverrideDto: {
            /** Format: int32 */
            id: number;
            entity: string;
            navigation: string;
            renameTo: null | string;
            hidden: boolean;
            issues: components["schemas"]["OverlayIssueDto"][];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        NavigationOverrideInput: {
            entity: string;
            navigation: string;
            renameTo?: null | string;
            /** @default false */
            hidden: boolean;
        };
        NumberFormat: {
            /** Format: int32 */
            decimals: null | number;
            prefix: null | string;
            suffix: null | string;
            compact: boolean;
        };
        OverlayCheckDto: {
            issues: components["schemas"]["OverlayIssueDto"][];
            breaks?: components["schemas"]["OverlayItemIssuesDto"][];
            navigations?: null | components["schemas"]["RelationNavigationsDto"];
            entity?: null | components["schemas"]["EntityDto"];
            diagnostics?: null | components["schemas"]["DiagnosticDto"][];
        };
        OverlayColumn: {
            name: string;
            hidden?: boolean;
            label?: null | string;
            type?: null | components["schemas"]["ScalarType"];
        };
        OverlayDto: {
            catalogVersion: string;
            relations: components["schemas"]["RelationDto"][];
            navigations: components["schemas"]["NavigationOverrideDto"][];
            virtualEntities: components["schemas"]["VirtualEntityDto"][];
            entitySettings: components["schemas"]["EntitySettingsDto"][];
            /** Format: int32 */
            errors: number;
            /** Format: int32 */
            warnings: number;
        };
        OverlayEntitySettings: {
            entity: string;
            key?: null | string[];
            displayColumn?: null | string;
            hidden?: boolean;
            columns?: components["schemas"]["OverlayColumn"][];
        };
        OverlayIssueDto: {
            code: string;
            severity: components["schemas"]["DiagnosticSeverity"];
            message: string;
        };
        OverlayItemDto: {
            kind: components["schemas"]["OverlayItemKind"];
            /** Format: int32 */
            id: number;
        };
        OverlayItemIssuesDto: {
            kind: components["schemas"]["OverlayItemKind"];
            /** Format: int32 */
            id: number;
            issues: components["schemas"]["OverlayIssueDto"][];
        };
        /** @enum {unknown} */
        OverlayItemKind: "relation" | "virtualEntity" | "entitySettings" | "navigation";
        OverlayNavigation: {
            entity: string;
            name: string;
            renameTo?: null | string;
            hidden?: boolean;
        };
        OverlayRelation: {
            from: string;
            fromColumns: string[];
            to: string;
            toColumns: string[];
            name?: null | string;
            inverseName?: null | string;
            description?: null | string;
        };
        OverlayVirtualEntity: {
            name: string;
            query: string;
            key?: null | string[];
            description?: null | string;
        };
        /** @enum {unknown} */
        PaletteAssign: "order" | "label";
        PaletteColor: {
            light: string;
            dark: null | string;
        };
        PaletteDashboardDto: {
            /** Format: int32 */
            id: number;
            name: string;
            owner: string;
        };
        PaletteDefinition: {
            colors: components["schemas"]["PaletteColor"][];
            assign: components["schemas"]["PaletteAssign"];
            distinct: boolean;
            whenOut: components["schemas"]["PaletteWhenOut"];
            matching: components["schemas"]["LabelMatching"];
            overrides: components["schemas"]["PaletteOverride"][];
        };
        PaletteDto: {
            /** Format: int32 */
            id: number;
            name: string;
            description: null | string;
            owner: string;
            isMine: boolean;
            canEdit: boolean;
            definition: components["schemas"]["PaletteDefinition"];
            hash: string;
            /** Format: int32 */
            usedBy: number;
            dashboards: components["schemas"]["PaletteDashboardDto"][];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        PaletteOverride: {
            label: null | string;
            color: components["schemas"]["PaletteColor"];
        };
        PaletteSummaryDto: {
            /** Format: int32 */
            id: number;
            name: string;
            description: null | string;
            owner: string;
            isMine: boolean;
            canEdit: boolean;
            colors: components["schemas"]["PaletteColor"][];
            assign: components["schemas"]["PaletteAssign"];
            /** Format: int32 */
            overrides: number;
            /** Format: int32 */
            usedBy: number;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        /** @enum {unknown} */
        PaletteWhenOut: "repeat" | "neutral";
        PasswordPolicyDto: {
            /** Format: int32 */
            minimumLength: number;
            /** Format: int32 */
            maximumLength: number;
        };
        PendingChangeDto: {
            /** Format: int64 */
            id: number;
            kind: components["schemas"]["PendingChangeKind"];
            entity: string;
            source: string;
            key: null | unknown[];
            rowId: null | string;
            tempId: null | string;
            values: Record<string, unknown>;
            original: Record<string, unknown>;
            display: Record<string, unknown>;
            /** Format: date-time */
            updatedAt: string;
        };
        /** @enum {unknown} */
        PendingChangeKind: "insert" | "update" | "delete";
        PermissionsDto: {
            canRead: boolean;
            canEditData: boolean;
            canAdmin: boolean;
        };
        PersonDto: {
            /** Format: int32 */
            id: number;
            userName: string;
            displayName: null | string;
        };
        Placement: {
            /** Format: int32 */
            x: number;
            /** Format: int32 */
            y: number;
            /** Format: int32 */
            w: number;
            /** Format: int32 */
            h: number;
        };
        PreviewScriptDto: {
            source: string;
            kind: string;
            dialect: string;
            text: string;
            editable: boolean;
            statements: components["schemas"]["PreviewStatementDto"][];
        };
        PreviewStatementDto: {
            /** Format: int64 */
            change: null | number;
            kind: components["schemas"]["DmlStatementKind"];
            description: string;
            text: string;
        };
        ProblemDetails: {
            type?: null | string;
            title?: null | string;
            /** Format: int32 */
            status?: null | number;
            detail?: null | string;
            instance?: null | string;
            /** @description What the problem is, for clients to tell problems apart by */
            code: string;
            /** @description The request's trace, to find it in the logs */
            traceId?: string;
        };
        PropertyChange: {
            property: string;
            from: null | string;
            to: null | string;
        };
        PublicDashboardDto: {
            name: string;
            description: null | string;
            definition: components["schemas"]["DashboardDefinition"];
            palettes: components["schemas"]["PublicPaletteDto"][];
        };
        PublicPaletteDto: {
            /** Format: int32 */
            id: number;
            colors: components["schemas"]["PaletteColor"][];
            assign: components["schemas"]["PaletteAssign"];
            distinct: boolean;
            whenOut: components["schemas"]["PaletteWhenOut"];
            matching: components["schemas"]["LabelMatching"];
        };
        PublicSettings: {
            showData: boolean;
        };
        PublishDashboardRequest: {
            /** Format: int32 */
            version: number;
            note?: null | string;
        };
        PublishedFilterRequest: {
            hash: string;
            state?: null | components["schemas"]["DashboardState"];
            search?: null | string;
        };
        PublishedWidgetRequest: {
            hash: string;
            state?: null | components["schemas"]["DashboardState"];
            page?: null | components["schemas"]["WidgetPage"];
            /** @default false */
            refresh: boolean;
        };
        QueryColumnDto: {
            name: string;
            type: components["schemas"]["TypeDto"];
        };
        QueryExplainDto: {
            queryText: string;
            parameters: components["schemas"]["QueryParameterDto"][];
            summary: string;
            diagnostics: components["schemas"]["DiagnosticDto"][];
            schema: null | components["schemas"]["ResultSchemaDto"];
            /** Format: int32 */
            plan: null | number;
            nodes: components["schemas"]["ExplainNodeDto"][];
            fragments: components["schemas"]["ExplainFragmentDto"][];
            mergeSql: null | string;
            mergeParameters: components["schemas"]["ExplainParameter"][];
            phases: null | components["schemas"]["ExplainPhase"][];
            text: string;
        };
        QueryExplainRequest: {
            text: string;
            parameters?: null | components["schemas"]["QueryParameterInput"][];
            grid?: null | components["schemas"]["GridStateDto"];
            /** @default false */
            verbose: boolean;
        };
        QueryLinkDto: {
            queryText: null | string;
            parameters: components["schemas"]["QueryParameterDto"][];
            browse: null | components["schemas"]["BrowseSourceDto"];
            key: null | unknown[];
        };
        QueryLinkRequest: {
            text: string;
            row: unknown[];
            parameters?: null | components["schemas"]["QueryParameterInput"][];
            /** Format: int32 */
            column?: null | number;
            /** Format: int32 */
            related?: null | number;
            catalogVersion?: null | string;
        };
        QueryPageDto: {
            queryText: string;
            parameters: components["schemas"]["QueryParameterDto"][];
            schema: null | components["schemas"]["ResultSchemaDto"];
            rows: components["schemas"]["ResultRowDto"][];
            /** Format: int64 */
            offset: number;
            hasMore: boolean;
            /** Format: int64 */
            total: null | number;
            stats: components["schemas"]["QueryStatsDto"];
            warnings: components["schemas"]["DiagnosticDto"][];
        };
        QueryPageRequest: {
            text: string;
            parameters?: null | components["schemas"]["QueryParameterInput"][];
            grid?: null | components["schemas"]["GridStateDto"];
            /** @default true */
            includeSchema: boolean;
            /** @default false */
            includeCount: boolean;
        };
        QueryParameterDto: {
            name: string;
            type: string;
            value: unknown;
        };
        QueryParameterInput: {
            name: string;
            type?: null | string;
            value?: unknown;
        };
        QueryStatsDto: {
            /** Format: double */
            elapsedMs: number;
            /** Format: int64 */
            fetchedRows: number;
            /** Format: int64 */
            keysSent: number;
            fragments: components["schemas"]["FragmentStatsDto"][];
        };
        QueryTextDto: {
            text: string;
            parameters: components["schemas"]["QueryParameterDto"][];
        };
        QueryTextRequest: {
            text: string;
            parameters?: null | components["schemas"]["QueryParameterInput"][];
        };
        QueryValidationDto: {
            success: boolean;
            complete: boolean;
            diagnostics: components["schemas"]["DiagnosticDto"][];
            parameters: components["schemas"]["UsedParameterDto"][];
            columns: null | components["schemas"]["QueryColumnDto"][];
        };
        /** @enum {unknown} */
        RefreshMode: "manual" | "interval";
        RefreshPolicy: {
            mode: components["schemas"]["RefreshMode"];
            /** Format: int32 */
            seconds: null | number;
        };
        RelationDto: {
            /** Format: int32 */
            id: number;
            from: string;
            fromColumns: string[];
            to: string;
            toColumns: string[];
            name: null | string;
            inverseName: null | string;
            description: null | string;
            navigations: null | components["schemas"]["RelationNavigationsDto"];
            issues: components["schemas"]["OverlayIssueDto"][];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        RelationInput: {
            from: string;
            fromColumns: string[];
            to: string;
            toColumns: string[];
            name?: null | string;
            inverseName?: null | string;
            description?: null | string;
        };
        RelationNavigationsDto: {
            forward: string;
            inverse: string;
        };
        /** @enum {unknown} */
        RelationOrigin: "foreignKey" | "overlay";
        /** @enum {unknown} */
        RelativeMode: "last" | "this" | "previous" | "toDate";
        RelativeRange: {
            unit: components["schemas"]["RelativeUnit"];
            /** Format: int32 */
            count: number;
            mode: components["schemas"]["RelativeMode"];
        };
        /** @enum {unknown} */
        RelativeUnit: "day" | "week" | "month" | "quarter" | "year";
        ResetPasswordRequest: {
            password: string;
        };
        ResultColumnDto: {
            name: string;
            /** Format: int32 */
            ordinal: number;
            type: components["schemas"]["TypeDto"];
            hidden: boolean;
            lineage: components["schemas"]["LineageDto"];
            link: null | components["schemas"]["ColumnLinkDto"];
            editTarget: null | components["schemas"]["EditTargetDto"];
        };
        ResultRowDto: {
            id: null | string;
            v: unknown[];
        };
        ResultSchemaDto: {
            columns: components["schemas"]["ResultColumnDto"][];
            rowIdentity: null | components["schemas"]["RowIdentityDto"];
        };
        RowIdentityDto: {
            entity: string;
            keyOrdinals: number[];
            related: components["schemas"]["ColumnLinkDto"][];
            capabilities: components["schemas"]["CapabilitiesDto"];
        };
        SavedQueryDto: {
            /** Format: int32 */
            id: number;
            name: string;
            description: null | string;
            text: string;
            parameters: components["schemas"]["QueryParameterInput"][];
            owner: string;
            isShared: boolean;
            isMine: boolean;
            canEdit: boolean;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        SavedQueryInput: {
            name: string;
            text: string;
            parameters?: null | components["schemas"]["QueryParameterInput"][];
            description?: null | string;
            /** @default false */
            isShared: boolean;
        };
        SavedQuerySummaryDto: {
            /** Format: int32 */
            id: number;
            name: string;
            description: null | string;
            owner: string;
            isShared: boolean;
            isMine: boolean;
            canEdit: boolean;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        /** @enum {unknown} */
        ScalarKind: "unknown" | "boolean" | "int16" | "int32" | "int64" | "decimal" | "single" | "double" | "string" | "binary" | "guid" | "date" | "time" | "dateTime" | "dateTimeOffset" | "interval" | "json";
        /** @description A logical type as the query language writes it: int64, decimal(10,2)?, string(50,ansi); ? when it may be null */
        ScalarType: string;
        SchemaChange: {
            change: components["schemas"]["SchemaChangeKind"];
            object: components["schemas"]["SchemaObject"];
            schema?: null | string;
            table?: null | string;
            name?: null | string;
            properties?: null | components["schemas"]["PropertyChange"][];
        };
        /** @enum {unknown} */
        SchemaChangeKind: "added" | "removed" | "changed";
        /** @enum {unknown} */
        SchemaObject: "source" | "table" | "column" | "primaryKey" | "uniqueKey" | "index" | "foreignKey";
        SchemaSnapshotDetailDto: {
            snapshot: components["schemas"]["SchemaSnapshotDto"];
            schema: components["schemas"]["SourceSchema"];
            changes: null | components["schemas"]["SchemaChange"][];
        };
        SchemaSnapshotDto: {
            /** Format: int64 */
            id: number;
            hash: string;
            /** Format: int32 */
            tableCount: number;
            /** Format: date-time */
            takenAt: string;
            /** Format: date-time */
            checkedAt: string;
            changes: null | components["schemas"]["ChangeCountsDto"];
        };
        /** @enum {unknown} */
        SchemaStatus: "notLoaded" | "loading" | "ready" | "failed";
        /** @enum {unknown} */
        SecretAction: "keep" | "set" | "clear";
        SecretInput: {
            action: components["schemas"]["SecretAction"];
            value?: null | string;
        };
        SecretStateDto: {
            hasValue: boolean;
        };
        /** @enum {unknown} */
        SelectionMode: "include" | "exclude";
        SelectionState: {
            mode: components["schemas"]["SelectionMode"];
            keys: unknown[][];
        };
        SessionDto: {
            signedIn: boolean;
            user: null | components["schemas"]["SessionUserDto"];
        };
        SessionUserDto: {
            /** Format: int32 */
            id: number;
            userName: string;
            displayName: null | string;
            role: components["schemas"]["UserRole"];
            mustChangePassword: boolean;
            permissions: components["schemas"]["PermissionsDto"];
        };
        SignInRequest: {
            userName: string;
            password: string;
        };
        /** @enum {unknown} */
        SortTarget: "dimension" | "measure" | "column";
        SourceField: {
            source: string;
            path: string[];
            column: string;
        };
        SourceSchema: {
            providerKind: string;
            serverVersion: null | string;
            defaultSchema: string;
            tables: components["schemas"]["TableSchema"][];
            warnings?: null | string[];
        };
        TableColumn: {
            field: components["schemas"]["FieldRef"];
            label: string;
            format: null | components["schemas"]["NumberFormat"];
        };
        /** @enum {unknown} */
        TableKind: "table" | "view" | "materializedView";
        /** @enum {unknown} */
        TableMode: "grouped" | "raw";
        TableSchema: {
            schema: string;
            name: string;
            kind: components["schemas"]["TableKind"];
            columns: components["schemas"]["ColumnSchema"][];
            primaryKey?: null | components["schemas"]["KeySchema"];
            uniqueKeys?: components["schemas"]["KeySchema"][];
            indexes?: components["schemas"]["IndexSchema"][];
            foreignKeys?: components["schemas"]["ForeignKeySchema"][];
            /** Format: int64 */
            rowCountEstimate?: null | number;
            hasTriggers?: boolean;
            comment?: null | string;
        };
        TestConnectionRequest: {
            kind: string;
            connection: components["schemas"]["ConnectionInput"];
            /** Format: int32 */
            connectionId?: null | number;
        };
        TrailCrumbDto: {
            entity?: null | string;
            navigation?: null | string;
            key?: null | unknown[];
        };
        TrailStepDto: {
            entity: null | string;
            label: string;
            title: unknown;
            found: null | boolean;
            problem: null | string;
        };
        TreeChildrenDto: {
            parent: null | string;
            nodes: components["schemas"]["TreeNodeDto"][];
        };
        TreeHitDto: {
            node: components["schemas"]["TreeNodeDto"];
            path: string[];
            columns: null | string[];
        };
        TreeNodeDto: {
            id: string;
            kind: components["schemas"]["TreeNodeKind"];
            name: string;
            hasChildren: boolean;
            label?: null | string;
            sourceKind?: null | string;
            status?: null | components["schemas"]["SchemaStatus"];
            isReadOnly?: null | boolean;
            /** Format: int64 */
            rows?: null | number;
            editable?: null | boolean;
            comment?: null | string;
        };
        /** @enum {unknown} */
        TreeNodeKind: "source" | "schema" | "folder" | "table" | "view" | "virtual";
        TreeSearchDto: {
            text: string;
            hits: components["schemas"]["TreeHitDto"][];
            more: boolean;
        };
        TypeDto: {
            kind: components["schemas"]["ScalarKind"];
            nullable: boolean;
            text: string;
            /** Format: int32 */
            precision?: null | number;
            /** Format: int32 */
            scale?: null | number;
            /** Format: int32 */
            length?: null | number;
            isAnsi?: null | boolean;
        };
        UpdateConnectionRequest: {
            connection: components["schemas"]["ConnectionInput"];
            /** Format: int32 */
            version: number;
            displayName?: null | string;
        };
        UpdateDashboardRequest: {
            name: string;
            description: null | string;
            definition: components["schemas"]["DashboardDefinition"];
            /** Format: int32 */
            version: number;
        };
        UpdateEntitySettingsRequest: {
            settings: components["schemas"]["EntitySettingsInput"];
            /** Format: int32 */
            version: number;
        };
        UpdateNavigationOverrideRequest: {
            navigation: components["schemas"]["NavigationOverrideInput"];
            /** Format: int32 */
            version: number;
        };
        UpdatePaletteRequest: {
            name: string;
            description: null | string;
            definition: components["schemas"]["PaletteDefinition"];
            /** Format: int32 */
            version: number;
        };
        UpdateRelationRequest: {
            relation: components["schemas"]["RelationInput"];
            /** Format: int32 */
            version: number;
        };
        UpdateSavedQueryRequest: {
            query: components["schemas"]["SavedQueryInput"];
            /** Format: int32 */
            version: number;
        };
        UpdateUserRequest: {
            role: components["schemas"]["UserRole"];
            isDisabled: boolean;
            /** Format: int32 */
            version: number;
            displayName?: null | string;
        };
        UpdateVirtualEntityRequest: {
            virtualEntity: components["schemas"]["VirtualEntityInput"];
            /** Format: int32 */
            version: number;
        };
        UsedParameterDto: {
            name: string;
            given: boolean;
            type: null | string;
        };
        UserDto: {
            /** Format: int32 */
            id: number;
            userName: string;
            displayName: null | string;
            role: components["schemas"]["UserRole"];
            isDisabled: boolean;
            mustChangePassword: boolean;
            /** Format: date-time */
            lockedOutUntil: null | string;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            lastSignInAt: null | string;
            /** Format: date-time */
            passwordChangedAt: string;
            /** Format: int32 */
            version: number;
        };
        /** @enum {unknown} */
        UserRole: "read" | "dataManager" | "admin";
        VirtualEntityDto: {
            /** Format: int32 */
            id: number;
            name: string;
            query: string;
            key: null | string[];
            description: null | string;
            issues: components["schemas"]["OverlayIssueDto"][];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            updatedAt: string;
            /** Format: int32 */
            version: number;
        };
        VirtualEntityInput: {
            name: string;
            query: string;
            key?: null | string[];
            description?: null | string;
        };
        VisibleWhenDto: {
            field: string;
            values: string[];
        };
        WidgetColumnDto: {
            name: string;
            role: components["schemas"]["ColumnRole"];
            /** Format: int32 */
            index: number;
            label: string;
            type: components["schemas"]["TypeDto"];
            format: null | components["schemas"]["NumberFormat"];
        };
        WidgetCondition: {
            field: components["schemas"]["FieldRef"];
            value: components["schemas"]["ConditionValue"];
        };
        WidgetConfig: components["schemas"]["WidgetConfigTextConfig"] | components["schemas"]["WidgetConfigPieConfig"] | components["schemas"]["WidgetConfigBarConfig"] | components["schemas"]["WidgetConfigLineConfig"] | components["schemas"]["WidgetConfigTableConfig"];
        WidgetConfigBarConfig: {
            /** @enum {string} */
            kind?: "bar";
            dimension: components["schemas"]["Dimension"];
            series: null | components["schemas"]["Dimension"];
            measures: components["schemas"]["Measure"][];
            sort: components["schemas"]["WidgetSort"];
            /** Format: int32 */
            limit: number;
            orientation: components["schemas"]["BarOrientation"];
            stack: components["schemas"]["BarStack"];
            labels: boolean;
            legend: components["schemas"]["LegendPosition"];
            xTitle: null | string;
            yTitle: null | string;
            /** Format: int32 */
            palette?: null | number;
            colorBy?: components["schemas"]["BarColorBy"];
            source: string;
            conditions: components["schemas"]["WidgetCondition"][];
            emits: boolean;
            listens: components["schemas"]["Listens"];
        };
        WidgetConfigLineConfig: {
            /** @enum {string} */
            kind?: "line";
            dimension: components["schemas"]["Dimension"];
            series: null | components["schemas"]["Dimension"];
            measures: components["schemas"]["Measure"][];
            /** Format: int32 */
            limit: number;
            area: boolean;
            gaps: components["schemas"]["LineGaps"];
            labels: boolean;
            legend: components["schemas"]["LegendPosition"];
            xTitle: null | string;
            yTitle: null | string;
            /** Format: int32 */
            palette?: null | number;
            source: string;
            conditions: components["schemas"]["WidgetCondition"][];
            emits: boolean;
            listens: components["schemas"]["Listens"];
        };
        WidgetConfigPieConfig: {
            /** @enum {string} */
            kind?: "pie";
            dimension: components["schemas"]["Dimension"];
            measure: components["schemas"]["Measure"];
            /** Format: int32 */
            limit: number;
            other: boolean;
            donut: boolean;
            labels: boolean;
            legend: components["schemas"]["LegendPosition"];
            /** Format: int32 */
            palette?: null | number;
            source: string;
            conditions: components["schemas"]["WidgetCondition"][];
            emits: boolean;
            listens: components["schemas"]["Listens"];
        };
        WidgetConfigTableConfig: {
            /** @enum {string} */
            kind?: "table";
            mode: components["schemas"]["TableMode"];
            dimensions: components["schemas"]["Dimension"][];
            measures: components["schemas"]["Measure"][];
            columns: components["schemas"]["TableColumn"][];
            sort: null | components["schemas"]["WidgetSort"];
            /** Format: int32 */
            pageSize: number;
            source: string;
            conditions: components["schemas"]["WidgetCondition"][];
            emits: boolean;
            listens: components["schemas"]["Listens"];
        };
        WidgetConfigTextConfig: {
            /** @enum {string} */
            kind?: "text";
            markdown: string;
        };
        WidgetDataDto: {
            columns: components["schemas"]["WidgetColumnDto"][];
            rows: unknown[][];
            truncated: boolean;
            /** Format: int64 */
            offset: number;
            /** Format: int64 */
            total: null | number;
            categories: null | unknown[];
            other: unknown;
            /** Format: date-time */
            refreshedAt: string;
            cached: boolean;
            issues: components["schemas"]["DashboardIssue"][];
            colors?: null | components["schemas"]["LabelColorDto"][];
        };
        WidgetPage: {
            /**
             * Format: int64
             * @default 0
             */
            offset: number;
            /** Format: int32 */
            limit?: null | number;
            /** @default false */
            count: boolean;
        };
        WidgetQueryDto: {
            main: components["schemas"]["QueryTextDto"];
            categories: null | components["schemas"]["QueryTextDto"];
            total: null | components["schemas"]["QueryTextDto"];
            underlying: components["schemas"]["QueryTextDto"];
        };
        WidgetSort: {
            by: components["schemas"]["SortTarget"];
            /** Format: int32 */
            index: number;
            descending: boolean;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type AdminEventDto = components['schemas']['AdminEventDto'];
export type Aggregate = components['schemas']['Aggregate'];
export type BarColorBy = components['schemas']['BarColorBy'];
export type BarOrientation = components['schemas']['BarOrientation'];
export type BarStack = components['schemas']['BarStack'];
export type Breakpoint = components['schemas']['Breakpoint'];
export type BrowseFromDto = components['schemas']['BrowseFromDto'];
export type BrowsePageDto = components['schemas']['BrowsePageDto'];
export type BrowsePageRequest = components['schemas']['BrowsePageRequest'];
export type BrowsePositionDto = components['schemas']['BrowsePositionDto'];
export type BrowsePositionRequest = components['schemas']['BrowsePositionRequest'];
export type BrowseSchemaDto = components['schemas']['BrowseSchemaDto'];
export type BrowseSourceDto = components['schemas']['BrowseSourceDto'];
export type BrowseTrailDto = components['schemas']['BrowseTrailDto'];
export type BrowseTrailRequest = components['schemas']['BrowseTrailRequest'];
export type Bucket = components['schemas']['Bucket'];
export type CapabilitiesDto = components['schemas']['CapabilitiesDto'];
export type CatalogDiagnosticDto = components['schemas']['CatalogDiagnosticDto'];
export type CatalogDto = components['schemas']['CatalogDto'];
export type CatalogOverlay = components['schemas']['CatalogOverlay'];
export type CatalogPathDto = components['schemas']['CatalogPathDto'];
export type CatalogPathStepDto = components['schemas']['CatalogPathStepDto'];
export type CatalogSourceDto = components['schemas']['CatalogSourceDto'];
export type ChangeCountsDto = components['schemas']['ChangeCountsDto'];
export type ChangeIssueDto = components['schemas']['ChangeIssueDto'];
export type ChangeOpDto = components['schemas']['ChangeOpDto'];
export type ChangeOpKind = components['schemas']['ChangeOpKind'];
export type ChangeOpsRequest = components['schemas']['ChangeOpsRequest'];
export type ChangePasswordRequest = components['schemas']['ChangePasswordRequest'];
export type ChangePreviewDto = components['schemas']['ChangePreviewDto'];
export type ChangeSetDto = components['schemas']['ChangeSetDto'];
export type ChartPaletteDto = components['schemas']['ChartPaletteDto'];
export type ChoiceDto = components['schemas']['ChoiceDto'];
export type ColumnLinkDto = components['schemas']['ColumnLinkDto'];
export type ColumnRole = components['schemas']['ColumnRole'];
export type ColumnSchema = components['schemas']['ColumnSchema'];
export type ColumnSettingDto = components['schemas']['ColumnSettingDto'];
export type CommitAuditDto = components['schemas']['CommitAuditDto'];
export type CommitAuditScriptDto = components['schemas']['CommitAuditScriptDto'];
export type CommitAuditSummaryDto = components['schemas']['CommitAuditSummaryDto'];
export type CommitChangesRequest = components['schemas']['CommitChangesRequest'];
export type CommitFailureDto = components['schemas']['CommitFailureDto'];
export type CommitResultDto = components['schemas']['CommitResultDto'];
export type CommitScriptDto = components['schemas']['CommitScriptDto'];
export type CommitScriptStatus = components['schemas']['CommitScriptStatus'];
export type CommitStatementDto = components['schemas']['CommitStatementDto'];
export type CommitStatus = components['schemas']['CommitStatus'];
export type ConditionOp = components['schemas']['ConditionOp'];
export type ConditionValue = components['schemas']['ConditionValue'];
export type ConnectionDto = components['schemas']['ConnectionDto'];
export type ConnectionInput = components['schemas']['ConnectionInput'];
export type ConnectionKindDto = components['schemas']['ConnectionKindDto'];
export type ConnectionMode = components['schemas']['ConnectionMode'];
export type ConnectionTestDto = components['schemas']['ConnectionTestDto'];
export type ConvertConnectionRequest = components['schemas']['ConvertConnectionRequest'];
export type CopyDashboardRequest = components['schemas']['CopyDashboardRequest'];
export type CreateConnectionRequest = components['schemas']['CreateConnectionRequest'];
export type CreateDashboardRequest = components['schemas']['CreateDashboardRequest'];
export type CreatePaletteRequest = components['schemas']['CreatePaletteRequest'];
export type CreateUserRequest = components['schemas']['CreateUserRequest'];
export type DashboardDefinition = components['schemas']['DashboardDefinition'];
export type DashboardDto = components['schemas']['DashboardDto'];
export type DashboardFilter = components['schemas']['DashboardFilter'];
export type DashboardIssue = components['schemas']['DashboardIssue'];
export type DashboardLayout = components['schemas']['DashboardLayout'];
export type DashboardLink = components['schemas']['DashboardLink'];
export type DashboardPermissionsDto = components['schemas']['DashboardPermissionsDto'];
export type DashboardPublicDto = components['schemas']['DashboardPublicDto'];
export type DashboardPublicRequest = components['schemas']['DashboardPublicRequest'];
export type DashboardRevisionDto = components['schemas']['DashboardRevisionDto'];
export type DashboardRevisionSummaryDto = components['schemas']['DashboardRevisionSummaryDto'];
export type DashboardSharing = components['schemas']['DashboardSharing'];
export type DashboardSharingDto = components['schemas']['DashboardSharingDto'];
export type DashboardSharingRequest = components['schemas']['DashboardSharingRequest'];
export type DashboardSource = components['schemas']['DashboardSource'];
export type DashboardState = components['schemas']['DashboardState'];
export type DashboardSummaryDto = components['schemas']['DashboardSummaryDto'];
export type DashboardVersionRequest = components['schemas']['DashboardVersionRequest'];
export type DashboardWidget = components['schemas']['DashboardWidget'];
export type DiagnosticDto = components['schemas']['DiagnosticDto'];
export type DiagnosticSeverity = components['schemas']['DiagnosticSeverity'];
export type Dimension = components['schemas']['Dimension'];
export type DmlFailureKind = components['schemas']['DmlFailureKind'];
export type DmlOutcome = components['schemas']['DmlOutcome'];
export type DmlScriptStatus = components['schemas']['DmlScriptStatus'];
export type DmlStatementKind = components['schemas']['DmlStatementKind'];
export type EditedScriptDto = components['schemas']['EditedScriptDto'];
export type EditTargetDto = components['schemas']['EditTargetDto'];
export type EntityColumnDto = components['schemas']['EntityColumnDto'];
export type EntityDto = components['schemas']['EntityDto'];
export type EntityKind = components['schemas']['EntityKind'];
export type EntityOverlayDto = components['schemas']['EntityOverlayDto'];
export type EntitySettingsDto = components['schemas']['EntitySettingsDto'];
export type EntitySettingsInput = components['schemas']['EntitySettingsInput'];
export type ExplainFragmentDto = components['schemas']['ExplainFragmentDto'];
export type ExplainNodeDto = components['schemas']['ExplainNodeDto'];
export type ExplainParameter = components['schemas']['ExplainParameter'];
export type ExplainPhase = components['schemas']['ExplainPhase'];
export type ExplainSubqueryDto = components['schemas']['ExplainSubqueryDto'];
export type FetchStrategy = components['schemas']['FetchStrategy'];
export type FieldDto = components['schemas']['FieldDto'];
export type FieldRef = components['schemas']['FieldRef'];
export type FieldType = components['schemas']['FieldType'];
export type FilterKind = components['schemas']['FilterKind'];
export type FilterValueDto = components['schemas']['FilterValueDto'];
export type FilterValuesDto = components['schemas']['FilterValuesDto'];
export type ForeignKeySchema = components['schemas']['ForeignKeySchema'];
export type FragmentStatsDto = components['schemas']['FragmentStatsDto'];
export type GridCollectionDto = components['schemas']['GridCollectionDto'];
export type GridColumnDto = components['schemas']['GridColumnDto'];
export type GridConditionDto = components['schemas']['GridConditionDto'];
export type GridFilterDto = components['schemas']['GridFilterDto'];
export type GridOp = components['schemas']['GridOp'];
export type GridReferenceDto = components['schemas']['GridReferenceDto'];
export type GridRowDto = components['schemas']['GridRowDto'];
export type GridSortDto = components['schemas']['GridSortDto'];
export type GridStateDto = components['schemas']['GridStateDto'];
export type GroupDto = components['schemas']['GroupDto'];
export type HealthCheckDto = components['schemas']['HealthCheckDto'];
export type HealthResponse = components['schemas']['HealthResponse'];
export type HealthStatus = components['schemas']['HealthStatus'];
export type HttpValidationProblemDetails = components['schemas']['HttpValidationProblemDetails'];
export type IndexSchema = components['schemas']['IndexSchema'];
export type InlineFilterRequest = components['schemas']['InlineFilterRequest'];
export type InlineWidgetRequest = components['schemas']['InlineWidgetRequest'];
export type InsertedRowDto = components['schemas']['InsertedRowDto'];
export type InsertMode = components['schemas']['InsertMode'];
export type IssueSeverity = components['schemas']['IssueSeverity'];
export type KeyDto = components['schemas']['KeyDto'];
export type KeySchema = components['schemas']['KeySchema'];
export type LabelColorDto = components['schemas']['LabelColorDto'];
export type LabelMatching = components['schemas']['LabelMatching'];
export type LayoutOverride = components['schemas']['LayoutOverride'];
export type LegendPosition = components['schemas']['LegendPosition'];
export type LineageDto = components['schemas']['LineageDto'];
export type LineageKind = components['schemas']['LineageKind'];
export type LineageSourceDto = components['schemas']['LineageSourceDto'];
export type LineGaps = components['schemas']['LineGaps'];
export type LinkKind = components['schemas']['LinkKind'];
export type ListenMode = components['schemas']['ListenMode'];
export type Listens = components['schemas']['Listens'];
export type Measure = components['schemas']['Measure'];
export type Multiplicity = components['schemas']['Multiplicity'];
export type NavigationDto = components['schemas']['NavigationDto'];
export type NavigationNamingOptions = components['schemas']['NavigationNamingOptions'];
export type NavigationOverlayDto = components['schemas']['NavigationOverlayDto'];
export type NavigationOverrideDto = components['schemas']['NavigationOverrideDto'];
export type NavigationOverrideInput = components['schemas']['NavigationOverrideInput'];
export type NumberFormat = components['schemas']['NumberFormat'];
export type OverlayCheckDto = components['schemas']['OverlayCheckDto'];
export type OverlayColumn = components['schemas']['OverlayColumn'];
export type OverlayDto = components['schemas']['OverlayDto'];
export type OverlayEntitySettings = components['schemas']['OverlayEntitySettings'];
export type OverlayIssueDto = components['schemas']['OverlayIssueDto'];
export type OverlayItemDto = components['schemas']['OverlayItemDto'];
export type OverlayItemIssuesDto = components['schemas']['OverlayItemIssuesDto'];
export type OverlayItemKind = components['schemas']['OverlayItemKind'];
export type OverlayNavigation = components['schemas']['OverlayNavigation'];
export type OverlayRelation = components['schemas']['OverlayRelation'];
export type OverlayVirtualEntity = components['schemas']['OverlayVirtualEntity'];
export type PaletteAssign = components['schemas']['PaletteAssign'];
export type PaletteColor = components['schemas']['PaletteColor'];
export type PaletteDashboardDto = components['schemas']['PaletteDashboardDto'];
export type PaletteDefinition = components['schemas']['PaletteDefinition'];
export type PaletteDto = components['schemas']['PaletteDto'];
export type PaletteOverride = components['schemas']['PaletteOverride'];
export type PaletteSummaryDto = components['schemas']['PaletteSummaryDto'];
export type PaletteWhenOut = components['schemas']['PaletteWhenOut'];
export type PasswordPolicyDto = components['schemas']['PasswordPolicyDto'];
export type PendingChangeDto = components['schemas']['PendingChangeDto'];
export type PendingChangeKind = components['schemas']['PendingChangeKind'];
export type PermissionsDto = components['schemas']['PermissionsDto'];
export type PersonDto = components['schemas']['PersonDto'];
export type Placement = components['schemas']['Placement'];
export type PreviewScriptDto = components['schemas']['PreviewScriptDto'];
export type PreviewStatementDto = components['schemas']['PreviewStatementDto'];
export type ProblemDetails = components['schemas']['ProblemDetails'];
export type PropertyChange = components['schemas']['PropertyChange'];
export type PublicDashboardDto = components['schemas']['PublicDashboardDto'];
export type PublicPaletteDto = components['schemas']['PublicPaletteDto'];
export type PublicSettings = components['schemas']['PublicSettings'];
export type PublishDashboardRequest = components['schemas']['PublishDashboardRequest'];
export type PublishedFilterRequest = components['schemas']['PublishedFilterRequest'];
export type PublishedWidgetRequest = components['schemas']['PublishedWidgetRequest'];
export type QueryColumnDto = components['schemas']['QueryColumnDto'];
export type QueryExplainDto = components['schemas']['QueryExplainDto'];
export type QueryExplainRequest = components['schemas']['QueryExplainRequest'];
export type QueryLinkDto = components['schemas']['QueryLinkDto'];
export type QueryLinkRequest = components['schemas']['QueryLinkRequest'];
export type QueryPageDto = components['schemas']['QueryPageDto'];
export type QueryPageRequest = components['schemas']['QueryPageRequest'];
export type QueryParameterDto = components['schemas']['QueryParameterDto'];
export type QueryParameterInput = components['schemas']['QueryParameterInput'];
export type QueryStatsDto = components['schemas']['QueryStatsDto'];
export type QueryTextDto = components['schemas']['QueryTextDto'];
export type QueryTextRequest = components['schemas']['QueryTextRequest'];
export type QueryValidationDto = components['schemas']['QueryValidationDto'];
export type RefreshMode = components['schemas']['RefreshMode'];
export type RefreshPolicy = components['schemas']['RefreshPolicy'];
export type RelationDto = components['schemas']['RelationDto'];
export type RelationInput = components['schemas']['RelationInput'];
export type RelationNavigationsDto = components['schemas']['RelationNavigationsDto'];
export type RelationOrigin = components['schemas']['RelationOrigin'];
export type RelativeMode = components['schemas']['RelativeMode'];
export type RelativeRange = components['schemas']['RelativeRange'];
export type RelativeUnit = components['schemas']['RelativeUnit'];
export type ResetPasswordRequest = components['schemas']['ResetPasswordRequest'];
export type ResultColumnDto = components['schemas']['ResultColumnDto'];
export type ResultRowDto = components['schemas']['ResultRowDto'];
export type ResultSchemaDto = components['schemas']['ResultSchemaDto'];
export type RowIdentityDto = components['schemas']['RowIdentityDto'];
export type SavedQueryDto = components['schemas']['SavedQueryDto'];
export type SavedQueryInput = components['schemas']['SavedQueryInput'];
export type SavedQuerySummaryDto = components['schemas']['SavedQuerySummaryDto'];
export type ScalarKind = components['schemas']['ScalarKind'];
export type ScalarType = components['schemas']['ScalarType'];
export type SchemaChange = components['schemas']['SchemaChange'];
export type SchemaChangeKind = components['schemas']['SchemaChangeKind'];
export type SchemaObject = components['schemas']['SchemaObject'];
export type SchemaSnapshotDetailDto = components['schemas']['SchemaSnapshotDetailDto'];
export type SchemaSnapshotDto = components['schemas']['SchemaSnapshotDto'];
export type SchemaStatus = components['schemas']['SchemaStatus'];
export type SecretAction = components['schemas']['SecretAction'];
export type SecretInput = components['schemas']['SecretInput'];
export type SecretStateDto = components['schemas']['SecretStateDto'];
export type SelectionMode = components['schemas']['SelectionMode'];
export type SelectionState = components['schemas']['SelectionState'];
export type SessionDto = components['schemas']['SessionDto'];
export type SessionUserDto = components['schemas']['SessionUserDto'];
export type SignInRequest = components['schemas']['SignInRequest'];
export type SortTarget = components['schemas']['SortTarget'];
export type SourceField = components['schemas']['SourceField'];
export type SourceSchema = components['schemas']['SourceSchema'];
export type TableColumn = components['schemas']['TableColumn'];
export type TableKind = components['schemas']['TableKind'];
export type TableMode = components['schemas']['TableMode'];
export type TableSchema = components['schemas']['TableSchema'];
export type TestConnectionRequest = components['schemas']['TestConnectionRequest'];
export type TrailCrumbDto = components['schemas']['TrailCrumbDto'];
export type TrailStepDto = components['schemas']['TrailStepDto'];
export type TreeChildrenDto = components['schemas']['TreeChildrenDto'];
export type TreeHitDto = components['schemas']['TreeHitDto'];
export type TreeNodeDto = components['schemas']['TreeNodeDto'];
export type TreeNodeKind = components['schemas']['TreeNodeKind'];
export type TreeSearchDto = components['schemas']['TreeSearchDto'];
export type TypeDto = components['schemas']['TypeDto'];
export type UpdateConnectionRequest = components['schemas']['UpdateConnectionRequest'];
export type UpdateDashboardRequest = components['schemas']['UpdateDashboardRequest'];
export type UpdateEntitySettingsRequest = components['schemas']['UpdateEntitySettingsRequest'];
export type UpdateNavigationOverrideRequest = components['schemas']['UpdateNavigationOverrideRequest'];
export type UpdatePaletteRequest = components['schemas']['UpdatePaletteRequest'];
export type UpdateRelationRequest = components['schemas']['UpdateRelationRequest'];
export type UpdateSavedQueryRequest = components['schemas']['UpdateSavedQueryRequest'];
export type UpdateUserRequest = components['schemas']['UpdateUserRequest'];
export type UpdateVirtualEntityRequest = components['schemas']['UpdateVirtualEntityRequest'];
export type UsedParameterDto = components['schemas']['UsedParameterDto'];
export type UserDto = components['schemas']['UserDto'];
export type UserRole = components['schemas']['UserRole'];
export type VirtualEntityDto = components['schemas']['VirtualEntityDto'];
export type VirtualEntityInput = components['schemas']['VirtualEntityInput'];
export type VisibleWhenDto = components['schemas']['VisibleWhenDto'];
export type WidgetColumnDto = components['schemas']['WidgetColumnDto'];
export type WidgetCondition = components['schemas']['WidgetCondition'];
export type WidgetConfig = components['schemas']['WidgetConfig'];
export type WidgetConfigBarConfig = components['schemas']['WidgetConfigBarConfig'];
export type WidgetConfigLineConfig = components['schemas']['WidgetConfigLineConfig'];
export type WidgetConfigPieConfig = components['schemas']['WidgetConfigPieConfig'];
export type WidgetConfigTableConfig = components['schemas']['WidgetConfigTableConfig'];
export type WidgetConfigTextConfig = components['schemas']['WidgetConfigTextConfig'];
export type WidgetDataDto = components['schemas']['WidgetDataDto'];
export type WidgetPage = components['schemas']['WidgetPage'];
export type WidgetQueryDto = components['schemas']['WidgetQueryDto'];
export type WidgetSort = components['schemas']['WidgetSort'];
export type $defs = Record<string, never>;
export interface operations {
    GetHealth: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["HealthResponse"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["HealthResponse"];
                };
            };
        };
    };
    GetSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SessionDto"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    SignIn: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SignInRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SessionDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    SignOut: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SessionDto"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ChangePassword: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ChangePasswordRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SessionDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetPasswordPolicy: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PasswordPolicyDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListUsers: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["UserDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateUser: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CreateUserRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["UserDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetUser: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["UserDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateUser: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateUserRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["UserDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteUser: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ResetUserPassword: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ResetPasswordRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["UserDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UnlockUser: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["UserDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListAdminEvents: {
        parameters: {
            query?: {
                before?: number;
                take?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AdminEventDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListCommits: {
        parameters: {
            query?: {
                before?: number;
                take?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CommitAuditSummaryDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetCommit: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CommitAuditDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListConnectionKinds: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionKindDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ConvertConnection: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                kind: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ConvertConnectionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionInput"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListConnections: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateConnection: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CreateConnectionRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetConnection: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateConnection: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateConnectionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteConnection: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    TestConnection: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionTestDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    TestConnectionSettings: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["TestConnectionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionTestDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    RefreshConnectionSchema: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConnectionDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListSchemaSnapshots: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SchemaSnapshotDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetSchemaSnapshot: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
                snapshotId: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SchemaSnapshotDetailDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetCatalog: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetTreeChildren: {
        parameters: {
            query?: {
                parent?: string;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["TreeChildrenDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    SearchTree: {
        parameters: {
            query: {
                text: string;
                take?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["TreeSearchDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetEntity: {
        parameters: {
            query: {
                name: string;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["EntityDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    FindCatalogPaths: {
        parameters: {
            query: {
                from: string;
                to: string;
                depth?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPathDto"][];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    BrowsePage: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["BrowsePageRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BrowsePageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    BrowseTrail: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["BrowseTrailRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BrowseTrailDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    BrowsePosition: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["BrowsePositionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BrowsePositionDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetOverlay: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OverlayDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ExportOverlay: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogOverlay"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetRelation: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RelationDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateRelation: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateRelationRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RelationDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteRelation: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateRelation: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["RelationInput"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RelationDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ValidateRelation: {
        parameters: {
            query?: {
                id?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["RelationInput"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OverlayCheckDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetNavigationOverride: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["NavigationOverrideDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateNavigationOverride: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateNavigationOverrideRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["NavigationOverrideDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteNavigationOverride: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateNavigationOverride: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["NavigationOverrideInput"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["NavigationOverrideDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ValidateNavigationOverride: {
        parameters: {
            query?: {
                id?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["NavigationOverrideInput"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OverlayCheckDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetVirtualEntity: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["VirtualEntityDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateVirtualEntity: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateVirtualEntityRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["VirtualEntityDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteVirtualEntity: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateVirtualEntity: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["VirtualEntityInput"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["VirtualEntityDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ValidateVirtualEntity: {
        parameters: {
            query?: {
                id?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["VirtualEntityInput"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OverlayCheckDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetEntitySettings: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["EntitySettingsDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateEntitySettings: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateEntitySettingsRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["EntitySettingsDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteEntitySettings: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateEntitySettings: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["EntitySettingsInput"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["EntitySettingsDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ValidateEntitySettings: {
        parameters: {
            query?: {
                id?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["EntitySettingsInput"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OverlayCheckDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ValidateQuery: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["QueryTextRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["QueryValidationDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ExplainQuery: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["QueryExplainRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["QueryExplainDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ExecuteQuery: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["QueryPageRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["QueryPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    FollowQueryLink: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["QueryLinkRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["QueryLinkDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListSavedQueries: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SavedQuerySummaryDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateSavedQuery: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SavedQueryInput"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SavedQueryDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetSavedQuery: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SavedQueryDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateSavedQuery: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateSavedQueryRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SavedQueryDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteSavedQuery: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetChanges: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ChangeSetDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ClearChanges: {
        parameters: {
            query?: {
                source?: string;
                entity?: string;
                version?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ChangeSetDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ApplyChangeOps: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ChangeOpsRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ChangeSetDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    PreviewChanges: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ChangePreviewDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CommitChanges: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CommitChangesRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CommitResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListDashboards: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardSummaryDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateDashboard: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CreateDashboardRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    FindPeople: {
        parameters: {
            query?: {
                text?: string;
                take?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetDashboard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateDashboard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateDashboardRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteDashboard: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetDashboardPalettes: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ChartPaletteDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    PublishDashboard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PublishDashboardRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DiscardDashboardChanges: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DashboardVersionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListDashboardRevisions: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardRevisionSummaryDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetDashboardRevision: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
                number: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardRevisionDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    RestoreDashboardRevision: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
                number: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DashboardVersionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ShareDashboard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DashboardSharingRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    SetDashboardPublic: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DashboardPublicRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    RegenerateDashboardLink: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DashboardVersionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CopyDashboard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CopyDashboardRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DashboardDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetSliceData: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["InlineWidgetRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WidgetDataDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetSliceQuery: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["InlineWidgetRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WidgetQueryDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetSliceFilterValues: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["InlineFilterRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["FilterValuesDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetWidgetData: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
                widget: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PublishedWidgetRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WidgetDataDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetWidgetQuery: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
                widget: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PublishedWidgetRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WidgetQueryDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetFilterValues: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
                filter: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PublishedFilterRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["FilterValuesDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListPalettes: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaletteSummaryDto"][];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreatePalette: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CreatePaletteRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaletteDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetPalette: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaletteDto"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdatePalette: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdatePaletteRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaletteDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeletePalette: {
        parameters: {
            query?: {
                version?: number;
            };
            header?: never;
            path: {
                id: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetPublicDashboard: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                token: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PublicDashboardDto"];
                };
            };
            /** @description Not Modified */
            304: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetPublicWidgetData: {
        parameters: {
            query?: {
                s?: string;
                offset?: number;
                limit?: number;
                count?: boolean;
            };
            header?: never;
            path: {
                token: string;
                widget: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WidgetDataDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetPublicFilterValues: {
        parameters: {
            query?: {
                s?: string;
                text?: string;
            };
            header?: never;
            path: {
                token: string;
                filter: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["FilterValuesDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Bad Gateway */
            502: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
}
