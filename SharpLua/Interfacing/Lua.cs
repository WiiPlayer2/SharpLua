using System.Text;
namespace SharpLua
{
    using System;
    using System.IO;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Reflection;
    using System.Threading;
    using System.Linq;
    using System.Diagnostics;

    /*
     * Main class of LuaInterface
     * Object-oriented wrapper to Lua API
     *
     * Author: Fabio Mascarenhas
     * Version: 1.0
     *
     * // steffenj: important changes in Lua class:
     * - removed all Open*Lib() functions
     * - all libs automatically open in the Lua class constructor (just assign nil to unwanted libs)
     * */

    /// <summary>
    /// The main interface to Lua. Provides a much more user-friendly API than the raw Lua API.
    /// </summary>
    public class LuaInterface : IDisposable, IReturnListHandler
    {
        /*readonly */
        private SharpLua.Lua.LuaState luaState;

        public Lua.LuaState LuaState { get { return luaState; } }

        ObjectTranslator translator;

        /// <summary>
        /// Gets/sets the object used to translate between Lua values and .NET values.
        /// </summary>
        /// <remarks>
        /// A Lua value is anything that may be stored in a Lua variable.
        /// A .NET value is anything that has copy-by-value semantics -- basically, all value types, and all object *references* (not the objects themselves).
        /// </remarks>
        public ObjectTranslator Translator { get { return translator; } set { translator = value; } }

        SharpLua.Lua.lua_CFunction panicCallback, lockCallback, unlockCallback;
        SharpLua.Lua.lua_CFunction tracebackFunction;
        // lockCallback, unlockCallback; used by debug code commented out for now

        public LuaInterface()
        {
            luaState = LuaDLL.luaL_newstate();	// steffenj: Lua 5.1.1 API change (lua_open is gone)
            luaState.initializing = true;
            //LuaDLL.luaopen_base(luaState);	// steffenj: luaopen_* no longer used
            LuaDLL.luaL_openlibs(luaState);		// steffenj: Lua 5.1.1 API change (luaopen_base is gone, just open all libs right here)
            LuaDLL.lua_pushstring(luaState, "LUAINTERFACE LOADED");
            LuaDLL.lua_pushboolean(luaState, true);
            LuaDLL.lua_settable(luaState, (int)LuaIndexes.LUA_REGISTRYINDEX);
            LuaDLL.lua_newtable(luaState);
            LuaDLL.lua_setglobal(luaState, "luanet");
            LuaDLL.lua_pushvalue(luaState, (int)LuaIndexes.LUA_GLOBALSINDEX);
            LuaDLL.lua_getglobal(luaState, "luanet");
            LuaDLL.lua_pushstring(luaState, "getmetatable");
            LuaDLL.lua_getglobal(luaState, "getmetatable");
            LuaDLL.lua_settable(luaState, -3);
            LuaDLL.lua_replace(luaState, (int)LuaIndexes.LUA_GLOBALSINDEX);
            translator = new ObjectTranslator(this, luaState);
            LuaDLL.lua_replace(luaState, (int)LuaIndexes.LUA_GLOBALSINDEX);

            tracebackFunction = new SharpLua.Lua.lua_CFunction(traceback);

            // We need to keep this in a managed reference so the delegate doesn't get garbage collected
            panicCallback = new SharpLua.Lua.lua_CFunction(PanicCallback);
            Lua.lua_CFunction oldpanicFunc =
                //LuaDLL.lua_atpanic(luaState, tracebackFunction);
                LuaDLL.lua_atpanic(luaState, panicCallback);

            DoString(ScriptStrings.InitLuaNet, "LuaNet");
            DoString(ScriptStrings.InitClrLib, "ClrLib");
            DoString(ScriptStrings.InitExtLib, "ExtLib");
            luaState.initializing = false;
            luaState.SetInterface(this);
        }

        private bool _StatePassed;

        /// <summary>
        /// CAUTION: LuaInterface's can't share the same lua state!
        /// </summary>
        /// <param name="lState"></param>
        public LuaInterface(SharpLua.Lua.LuaState lState)
        {
            lState.initializing = true;
            LuaDLL.lua_pushstring(lState, "LUAINTERFACE LOADED");
            LuaDLL.lua_gettable(lState, (int)LuaIndexes.LUA_REGISTRYINDEX);

            if (LuaDLL.lua_toboolean(lState, -1))
            {
                LuaDLL.lua_settop(lState, -2);
                throw new LuaException("There is already a LuaInterface associated with this LuaState");
            }
            else
            {
                LuaDLL.lua_settop(lState, -2);
                LuaDLL.lua_pushstring(lState, "LUAINTERFACE LOADED");
                LuaDLL.lua_pushboolean(lState, true);
                LuaDLL.lua_settable(lState, (int)LuaIndexes.LUA_REGISTRYINDEX);
                this.luaState = lState;
                LuaDLL.lua_pushvalue(lState, (int)LuaIndexes.LUA_GLOBALSINDEX);
                LuaDLL.lua_getglobal(lState, "luanet");
                LuaDLL.lua_pushstring(lState, "getmetatable");
                LuaDLL.lua_getglobal(lState, "getmetatable");
                LuaDLL.lua_settable(lState, -3);
                LuaDLL.lua_replace(lState, (int)LuaIndexes.LUA_GLOBALSINDEX);
                translator = new ObjectTranslator(this, this.luaState);
                LuaDLL.lua_replace(lState, (int)LuaIndexes.LUA_GLOBALSINDEX);
                DoString(ScriptStrings.InitLuaNet, "LuaNet");
                DoString(ScriptStrings.InitClrLib, "ClrLib");
                DoString(ScriptStrings.InitExtLib, "ExtLib");
            }
            _StatePassed = true;
            lState.initializing = false;
        }

        public void Close()
        {
            if (_StatePassed)
                return;

            if (luaState != null)
                LuaDLL.lua_close(luaState);

            luaState = null;
            //luaState = IntPtr.Zero; <- suggested by Christopher Cebulski http://luaforge.net/forum/forum.php?thread_id=44593&forum_id=146
        }

        /// <summary>
        /// Called for each lua_lock call
        /// </summary>
        /// <param name="luaState"></param>
        /// Not yet used
        int LockCallback(SharpLua.Lua.LuaState luaState)
        {
            return 0;
        }

        /// <summary>
        /// Called for each lua_unlock call
        /// </summary>
        /// <param name="luaState"></param>
        /// Not yet used
        int UnlockCallback(SharpLua.Lua.LuaState luaState)
        {
            // Monitor.Exit(luaLock);

            return 0;
        }

        static int PanicCallback(SharpLua.Lua.LuaState luaState)
        {
            // string desc = LuaDLL.lua_tostring(luaState, 1);

            // string desc = LuaDLL.lua_tostring(luaState, 1);
            string reason = String.Format("unprotected error in call to Lua API ({0})", LuaDLL.lua_tostring(luaState, -1));

            //        lua_tostring(L, -1);

            throw new LuaException(reason);
        }



        /// <summary>
        /// Assuming we have a Lua error string sitting on the stack, throw a C# exception out to the user's app
        /// </summary>
        /// <exception cref="LuaScriptException">Thrown if the script caused an exception</exception>
        void ThrowExceptionFromError(int oldTop)
        {
            Exception innerException = null;
            string msg = "";
            if (Lua.lua_isstring(luaState, -1) == 1)
                msg = Lua.lua_tostring(luaState, -1);
            else if (Lua.lua_isnumber(luaState, -1) == 1
                     || Lua.lua_isboolean(luaState, -1)
                     || Lua.lua_iscfunction(luaState, -1)
                     || Lua.lua_isfunction(luaState, -1)
                     || Lua.lua_isthread(luaState, -1)
                     || Lua.lua_istable(luaState, -1)
                    )
                     msg = "Unknown Lua Error";
            else
            {
                object err = translator.getObject(luaState, -1);
                innerException = err as Exception;
                LuaDLL.lua_settop(luaState, oldTop);

                // A pre-wrapped exception - just rethrow it (stack trace of InnerException will be preserved)
                LuaException luaEx = err as LuaException;
                if (luaEx != null)
                    throw luaEx;

                // A non-wrapped Lua error (best interpreted as a string) - wrap it and throw it
                if (err == null)
                    msg = "Unknown Lua Error";
                else
                    msg = err.ToString();
            }
            throw new LuaException(msg, innerException);
        }



        /// <summary>
        /// Convert C# exceptions into Lua errors
        /// </summary>
        /// <returns>num of things on stack</returns>
        /// <param name="e">null for no pending exception</param>
        internal int SetPendingException(object e)
        {
            object caughtExcept = e;

            if (caughtExcept != null)
            {
                translator.throwError(luaState, caughtExcept);
                LuaDLL.lua_pushnil(luaState);

                return 1;
            }
            else
                return 0;
        }

        private bool executing;

        /// <summary>
        /// True while a script is being executed
        /// </summary>
        public bool IsExecuting { get { return executing; } }

        /// <summary>
        ///
        /// </summary>
        /// <param name="chunk"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public LuaFunction LoadString(string chunk, string name)
        {
            int oldTop = LuaDLL.lua_gettop(luaState);

            executing = true;
            try
            {
                if (LuaDLL.luaL_loadbuffer(luaState, chunk, chunk.Length, name) != 0)
                    ThrowExceptionFromError(oldTop);
            }
            finally { executing = false; }

            LuaFunction result = translator.getFunction(luaState, -1);
            translator.popValues(luaState, oldTop);

            return result;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public LuaFunction LoadFile(string fileName)
        {
            int oldTop = LuaDLL.lua_gettop(luaState);
            if (LuaDLL.luaL_loadfile(luaState, fileName) != 0)
                ThrowExceptionFromError(oldTop);

            LuaFunction result = translator.getFunction(luaState, -1);
            translator.popValues(luaState, oldTop);

            return result;
        }


        /*
         * Excutes a Lua chunk and returns all the chunk's return
         * values in an array
         */
        public object[] DoString(string chunk)
        {
            return DoString(chunk, "chunk");
        }

        delegate int LoadBufferFunc<T>(Lua.LuaState L, T chunk, int chunkSize, string chunkName);

        /// <summary>
        /// Executes a Lua chunk (specified as a .NET string) and returns all the chunk's return values in an array.
        /// </summary>
        /// <param name="chunk">Chunk to execute</param>
        /// <param name="chunkName">Name to associate with the chunk</param>
        /// <param name="args">Optional set of arguments to pass to chunk (accessible from '...')</param>
        /// <returns>Array of objects as returned by the chunk.</returns>
        public object[] DoString(string chunk, string chunkName, params object[] args)
        {
            return DoBufferCommon<string>(LuaDLL.luaL_loadbuffer, LoadFuncFlags.None, chunk, chunk.Length, chunkName, args, -1, this);
        }

        public object[] DoString(string chunk, string chunkName, object[] args, IReturnListHandler retxlat)
        {
            return DoBufferCommon<string>(LuaDLL.luaL_loadbuffer, LoadFuncFlags.None, chunk, chunk.Length, chunkName, args, -1, retxlat);
        }

        public object[] DoString(char[] chunk)
        {
            return DoString(chunk, "chunk");
        }

        /// <summary>
        /// Executes a Lua chunk (specified in a character array) and returns all the chunk's return values in an array.
        /// </summary>
        /// <param name="chunk">Chunk to execute</param>
        /// <param name="chunkName">Name to associate with the chunk</param>
        /// <param name="args">Optional set of arguments to pass to chunk (accessible from '...')</param>
        /// <returns>Array of objects as returned by the chunk.</returns>
        public object[] DoString(char[] chunk, string chunkName, params object[] args)
        {
            return DoBufferCommon<char[]>(LuaDLL.luaL_loadbuffer, LoadFuncFlags.None, chunk, chunk.Length, chunkName, args, -1);
        }

        private int traceback(SharpLua.Lua.LuaState luaState)
        {
            LuaDLL.lua_getglobal(luaState, "debug");
            LuaDLL.lua_getfield(luaState, -1, "traceback");
            LuaDLL.lua_pushvalue(luaState, 1);
            LuaDLL.lua_pushnumber(luaState, 2);
            LuaDLL.lua_call(luaState, 2, 1);
            return 1;
        }

        [Flags]
        enum LoadFuncFlags
        {
            None = 0,
            /// <summary>
            /// Error messages end up on the top of the stack, above the arguments, instead of below them. (luaL_loadfile)
            /// </summary>
            ErrorOnTop = (1<<0),
        }

        /// <summary>
        /// Excutes a Lua file and returns all the chunk's return
        /// values in an array
        /// </summary>
        /// <param name="fileName">File to be executed</param>
        /// <param name="args">Optional array of arguments.</param>
        /// <param name="arg0_idx">Offset into <paramref name="args"/> where the 'first' argument resides, minus 1.
        /// Ignored if args is null or zero-length.
        /// </param>
        /// <returns></returns>
        public object[] DoFile(string fileName, object[] args, int arg0_idx)
        {
            if (fileName == "-") fileName = null;
            return DoBufferCommon<string>(luaL_loadfile_shim, LoadFuncFlags.ErrorOnTop, null, 0, fileName, args, arg0_idx);
        }

        static int luaL_loadfile_shim(Lua.LuaState L, string unused1, int unused2, string filename) 
        {
            return LuaDLL.luaL_loadfile(L, filename);
        }

        int PushChunkArguments(object[] args, int arg0_idx)
        {
            int pushed_args = 0;
            if (args != null)
            {
                // based on 'getargs' from lua.c
                // I honestly have no understanding as to how it works, but it seems to, so okay...
                int num_real_args = args.Length - (arg0_idx + 1);
                for (int i = arg0_idx + 1; i < args.Length; i++)
                {
                    translator.push(luaState, args[i]);
                    pushed_args++;
                }
                if (arg0_idx >= 0)
                {
                    // This looks like it constructs the 'args' table.
                    LuaDLL.lua_createtable(luaState, num_real_args, args.Length);
                    for (int i = 0; i < args.Length; i++)
                    {
                        translator.push(luaState, args[i]);
                        LuaDLL.lua_rawseti(luaState, -2, i - arg0_idx);
                    }
                    // this was actually in handle_script() from lua.c
                    LuaDLL.lua_setglobal(luaState, "arg");
                }
            }

            return pushed_args;
        }

        object[] DoBufferCommon<T>(LoadBufferFunc<T> loadfunc, LoadFuncFlags flags, T chunk, int chunksize, string chunkName, object[] args, int arg0_idx)
        {
            return DoBufferCommon<T>(loadfunc, flags, chunk, chunksize, chunkName, args, arg0_idx, this);
        }
        

        object[] DoBufferCommon<T>(LoadBufferFunc<T> loadfunc, LoadFuncFlags flags, T chunk, int chunksize, string chunkName, object[] args, int arg0_idx, IReturnListHandler xlate)
        {
            if (xlate == null) xlate = this;
            int oldTop = LuaDLL.lua_gettop(luaState);

            int pushed_args = PushChunkArguments(args, arg0_idx);

            int loadfunc_result = loadfunc(luaState, chunk, chunksize, chunkName);

            if (loadfunc_result == 0)
            {
                if (pushed_args > 0)
                {
                    // the 'file' now exists as a function at the top of the stack.
                    // If we have arguments to pass to it, we need to adjust the stack so they are ABOVE our script
                    // this can be seen in handle_script in lua.c
                    LuaDLL.lua_insert(luaState, -(pushed_args + 1));
                }

                int docall_base = LuaDLL.lua_gettop(luaState) - pushed_args;
                LuaDLL.lua_pushstdcallcfunction(luaState, tracebackFunction);
                LuaDLL.lua_insert(luaState, docall_base);

                executing = true;
                try
                {
                    if (LuaDLL.lua_pcall(luaState, pushed_args, xlate.NumResults, docall_base) == 0)
                    {
                        object[] result = xlate.PopValues(luaState, oldTop + 2);
                        Debug.Assert(luaState.top >= oldTop + 2);
                        LuaDLL.lua_settop(luaState, oldTop);
                        return result;
                    }
                    else
                        ThrowExceptionFromError(oldTop);
                }
                finally { executing = false; }
            }
            else
            {
                if (pushed_args > 0)
                {
                    if ((flags & LoadFuncFlags.ErrorOnTop) != 0)
                    {
                        // Move the top element over the first pushed arg
                        LuaDLL.lua_replace(luaState, -(pushed_args+1));
                        // Don't pop that space!
                        pushed_args--;
                    }

                    LuaDLL.lua_pop(luaState, pushed_args);
                }

                //string s = Lua.lua_tostring(luaState, -1);
                //if (s.EndsWith("error #-1"))
                //{
                //    Lua.lua_pushstring(luaState, "unable to open " + fileName + ": file not found");
                //}
                ThrowExceptionFromError(LuaDLL.lua_gettop(luaState));
            }

            return null;            // Never reached - keeps compiler happy
        }


        /*
         * Indexer for global variables from the LuaInterpreter
         * Supports navigation of tables by using . operator
         */
        public object this[string fullPath]
        {
            get
            {
                object returnValue = null;
                int oldTop = LuaDLL.lua_gettop(luaState);
                string[] path = fullPath.Split(new char[] { '.' });
                LuaDLL.lua_getglobal(luaState, path[0]);
                returnValue = translator.getObject(luaState, -1);
                if (path.Length > 1)
                {
                    string[] remainingPath = new string[path.Length - 1];
                    Array.Copy(path, 1, remainingPath, 0, path.Length - 1);
                    returnValue = getObject(remainingPath);
                }
                LuaDLL.lua_settop(luaState, oldTop);
                return returnValue;
            }
            set
            {
                int oldTop = LuaDLL.lua_gettop(luaState);
                string[] path = fullPath.Split(new char[] { '.' });
                if (path.Length == 1)
                {
                    translator.push(luaState, value);
                    LuaDLL.lua_setglobal(luaState, fullPath);
                }
                else
                {
                    LuaDLL.lua_getglobal(luaState, path[0]);
                    string[] remainingPath = new string[path.Length - 1];
                    Array.Copy(path, 1, remainingPath, 0, path.Length - 1);
                    setObject(remainingPath, value);
                }
                LuaDLL.lua_settop(luaState, oldTop);
            }
        }

        #region Globals auto-complete
        private readonly List<string> globals = new List<string>();
        private bool globalsSorted;

        /// <summary>
        /// An alphabetically sorted list of all globals (objects, methods, etc.) externally added to this Lua instance
        /// </summary>
        /// <remarks>Members of globals are also listed. The formatting is optimized for text input auto-completion.</remarks>
        public IEnumerable<string> Globals
        {
            get
            {
                // Only sort list when necessary
                if (!globalsSorted)
                {
                    globals.Sort();
                    globalsSorted = true;
                }

                return globals;
            }
        }

        /// <summary>
        /// Adds an entry to <see cref="globals"/> (recursivley handles 2 levels of members)
        /// </summary>
        /// <param name="path">The index accessor path ot the entry</param>
        /// <param name="type">The type of the entry</param>
        /// <param name="recursionCounter">How deep have we gone with recursion?</param>
        private void registerGlobal(string path, Type type, int recursionCounter)
        {
            // If the type is a global method, list it directly
            if (type == typeof(SharpLua.Lua.lua_CFunction))
            {
                // Format for easy method invocation
                globals.Add(path + "(");
            }
            // If the type is a class or an interface and recursion hasn't been running too long, list the members
            else if ((type.IsClass || type.IsInterface) && type != typeof(string) && recursionCounter < 2)
            {
                #region Methods
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (
                        // Check that the LuaHideAttribute and LuaGlobalAttribute were not applied
                        (method.GetCustomAttributes(typeof(LuaHideAttribute), false).Length == 0) &&
                        (method.GetCustomAttributes(typeof(LuaGlobalAttribute), false).Length == 0) &&
                        // Exclude some generic .NET methods that wouldn't be very usefull in Lua
                        method.Name != "GetType" && method.Name != "GetHashCode" && method.Name != "Equals" &&
                        method.Name != "ToString" && method.Name != "Clone" && method.Name != "Dispose" &&
                        method.Name != "GetEnumerator" && method.Name != "CopyTo" &&
                        !method.Name.StartsWith("get_", StringComparison.Ordinal) &&
                        !method.Name.StartsWith("set_", StringComparison.Ordinal) &&
                        !method.Name.StartsWith("add_", StringComparison.Ordinal) &&
                        !method.Name.StartsWith("remove_", StringComparison.Ordinal))
                    {
                        // Format for easy method invocation
                        string command = path + ":" + method.Name + "(";
                        if (method.GetParameters().Length == 0) command += ")";
                        globals.Add(command);
                    }
                }
                #endregion

                #region Fields
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (
                        // Check that the LuaHideAttribute and LuaGlobalAttribute were not applied
                        (field.GetCustomAttributes(typeof(LuaHideAttribute), false).Length == 0) &&
                        (field.GetCustomAttributes(typeof(LuaGlobalAttribute), false).Length == 0))
                    {
                        // Go into recursion for members
                        registerGlobal(path + "." + field.Name, field.FieldType, recursionCounter + 1);
                    }
                }
                #endregion

                #region Properties
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (
                        // Check that the LuaHideAttribute and LuaGlobalAttribute were not applied
                        (property.GetCustomAttributes(typeof(LuaHideAttribute), false).Length == 0) &&
                        (property.GetCustomAttributes(typeof(LuaGlobalAttribute), false).Length == 0)
                        // Exclude some generic .NET properties that wouldn't be very usefull in Lua
                        && property.Name != "Item")
                    {
                        // Go into recursion for members
                        registerGlobal(path + "." + property.Name, property.PropertyType, recursionCounter + 1);
                    }
                }
                #endregion
            }
            // Otherwise simply add the element to the list
            else globals.Add(path);

            // List will need to be sorted on next access
            globalsSorted = false;
        }
        #endregion

        /*
         * Navigates a table in the top of the stack, returning
         * the value of the specified field
         */
        internal object getObject(string[] remainingPath)
        {
            object returnValue = null;
            for (int i = 0; i < remainingPath.Length; i++)
            {
                LuaDLL.lua_pushstring(luaState, remainingPath[i]);
                LuaDLL.lua_gettable(luaState, -2);
                returnValue = translator.getObject(luaState, -1);
                if (returnValue == null) break;
            }
            return returnValue;
        }
        /*
         * Gets a numeric global variable
         */
        public double GetNumber(string fullPath)
        {
            return (double)this[fullPath];
        }
        /*
         * Gets a string global variable
         */
        public string GetString(string fullPath)
        {
            return (string)this[fullPath];
        }
        /*
         * Gets a table global variable
         */
        public LuaTable GetTable(string fullPath)
        {
            return (LuaTable)this[fullPath];
        }
        /*
         * Gets a table global variable as an object implementing
         * the interfaceType interface
         */
#if !WindowsCE
        public object GetTable(Type interfaceType, string fullPath)
        {
            return CodeGeneration.Instance.GetClassInstance(interfaceType, GetTable(fullPath));
        }
#endif
        /*
         * Gets a function global variable
         */
        public LuaFunction GetFunction(string fullPath)
        {
            object obj = this[fullPath];
            return (obj is SharpLua.Lua.lua_CFunction ? new LuaFunction((SharpLua.Lua.lua_CFunction)obj, this) : (LuaFunction)obj);
        }
        /*
         * Gets a function global variable as a delegate of
         * type delegateType
         */
#if !WindowsCE
        public Delegate GetFunction(Type delegateType, string fullPath)
        {
            return CodeGeneration.Instance.GetDelegate(delegateType, GetFunction(fullPath));
        }
#endif
        /*
         * Calls the object as a function with the provided arguments,
         * returning the function's returned values inside an array
         */
        internal object[] callFunction(object function, object[] args)
        {
            return callFunction(function, args, null);
        }


        /*
         * Calls the object as a function with the provided arguments and
         * casting returned values to the types in returnTypes before returning
         * them in an array
         */
        internal object[] callFunction(object function, object[] args, Type[] returnTypes)
        {
            int nArgs = 0;
            int oldTop = LuaDLL.lua_gettop(luaState);
            if (!LuaDLL.lua_checkstack(luaState, args.Length + 6))
                throw new LuaException("Lua stack overflow");
            translator.push(luaState, function);
            if (args != null)
            {
                nArgs = args.Length;
                for (int i = 0; i < args.Length; i++)
                {
                    translator.push(luaState, args[i]);
                }
            }
            executing = true;
            try
            {
                int error = LuaDLL.lua_pcall(luaState, nArgs, -1, 0);
                if (error != 0)
                    ThrowExceptionFromError(oldTop);
            }
            finally { executing = false; }

            if (returnTypes != null)
                return translator.popValues(luaState, oldTop, returnTypes);
            else
                return translator.popValues(luaState, oldTop);
        }
        /*
         * Navigates a table to set the value of one of its fields
         */
        internal void setObject(string[] remainingPath, object val)
        {
            for (int i = 0; i < remainingPath.Length - 1; i++)
            {
                LuaDLL.lua_pushstring(luaState, remainingPath[i]);
                LuaDLL.lua_gettable(luaState, -2);
            }
            LuaDLL.lua_pushstring(luaState, remainingPath[remainingPath.Length - 1]);
            translator.push(luaState, val);
            LuaDLL.lua_settable(luaState, -3);
        }
        /*
         * Creates a new table as a global variable or as a field
         * inside an existing table
         */
        public LuaTable NewTable(string fullPath)
        {
            string[] path = fullPath.Split(new char[] { '.' });
            int oldTop = LuaDLL.lua_gettop(luaState);
            if (path.Length == 1)
            {
                LuaDLL.lua_newtable(luaState);
                LuaDLL.lua_setglobal(luaState, fullPath);
            }
            else
            {
                LuaDLL.lua_getglobal(luaState, path[0]);
                for (int i = 1; i < path.Length - 1; i++)
                {
                    LuaDLL.lua_pushstring(luaState, path[i]);
                    LuaDLL.lua_gettable(luaState, -2);
                }
                LuaDLL.lua_pushstring(luaState, path[path.Length - 1]);
                LuaDLL.lua_newtable(luaState);
                LuaDLL.lua_settable(luaState, -3);
            }
            LuaDLL.lua_settop(luaState, oldTop);
            return GetTable(fullPath);
        }

        public ListDictionary GetTableDict(LuaTable table)
        {
            ListDictionary dict = new ListDictionary();

            int oldTop = LuaDLL.lua_gettop(luaState);
            translator.push(luaState, table);
            LuaDLL.lua_pushnil(luaState);
            while (LuaDLL.lua_next(luaState, -2) != 0)
            {
                dict[translator.getObject(luaState, -2)] = translator.getObject(luaState, -1);
                LuaDLL.lua_settop(luaState, -2);
            }
            LuaDLL.lua_settop(luaState, oldTop);

            return dict;
        }

        /*
         * Lets go of a previously allocated reference to a table, function
         * or userdata
         */

        internal void dispose(int reference)
        {
            if (luaState != null) //Fix submitted by Qingrui Li
                LuaDLL.lua_unref(luaState, reference);
        }
        /*
         * Gets a field of the table corresponding to the provided reference
         * using rawget (do not use metatables)
         */
        internal object rawGetObject(int reference, string field)
        {
            int oldTop = LuaDLL.lua_gettop(luaState);
            LuaDLL.lua_getref(luaState, reference);
            LuaDLL.lua_pushstring(luaState, field);
            LuaDLL.lua_rawget(luaState, -2);
            object obj = translator.getObject(luaState, -1);
            LuaDLL.lua_settop(luaState, oldTop);
            return obj;
        }
        /*
         * Gets a field of the table or userdata corresponding to the provided reference
         */
        internal object getObject(int reference, string field)
        {
            int oldTop = LuaDLL.lua_gettop(luaState);
            LuaDLL.lua_getref(luaState, reference);
            object returnValue = getObject(field.Split(new char[] { '.' }));
            LuaDLL.lua_settop(luaState, oldTop);
            return returnValue;
        }
        /*
         * Gets a numeric field of the table or userdata corresponding the the provided reference
         */
        internal object getObject(int reference, object field)
        {
            int oldTop = LuaDLL.lua_gettop(luaState);
            LuaDLL.lua_getref(luaState, reference);
            translator.push(luaState, field);
            LuaDLL.lua_gettable(luaState, -2);
            object returnValue = translator.getObject(luaState, -1);
            LuaDLL.lua_settop(luaState, oldTop);
            return returnValue;
        }
        /*
         * Sets a field of the table or userdata corresponding the the provided reference
         * to the provided value
         */
        internal void setObject(int reference, string field, object val)
        {
            int oldTop = LuaDLL.lua_gettop(luaState);
            LuaDLL.lua_getref(luaState, reference);
            setObject(field.Split(new char[] { '.' }), val);
            LuaDLL.lua_settop(luaState, oldTop);
        }
        /*
         * Sets a numeric field of the table or userdata corresponding the the provided reference
         * to the provided value
         */
        internal void setObject(int reference, object field, object val)
        {
            int oldTop = LuaDLL.lua_gettop(luaState);
            LuaDLL.lua_getref(luaState, reference);
            translator.push(luaState, field);
            translator.push(luaState, val);
            LuaDLL.lua_settable(luaState, -3);
            LuaDLL.lua_settop(luaState, oldTop);
        }

#if WindowsCE

        public LuaFunction RegisterFunction(string path, Delegate d)
        {
            if (d == null) throw new ArgumentNullException("d");
            // Under CE, we can't get to the underlying object or method
            // But we don't actually have to -- we can use 'd' itself and the delegate's Invoke method.

            return RegisterFunction(path,
                (object)d,
                d.GetType().GetMethod("Invoke")
                );
        }
#else
        public LuaFunction RegisterFunction(string path, Delegate d)
        {
            return RegisterFunction(path, d.Target, d.Method);
        }
#endif

        /*
         * Registers an object's method as a Lua function (global or table field)
         * The method may have any signature
         */
        public LuaFunction RegisterFunction(string path, object target, MethodBase function /*MethodInfo function*/)  //CP: Fix for struct constructor by Alexander Kappner (link: http://luaforge.net/forum/forum.php?thread_id=2859&forum_id=145)
        {
            // We leave nothing on the stack when we are done
            int oldTop = LuaDLL.lua_gettop(luaState);

            LuaMethodWrapper wrapper = new LuaMethodWrapper(translator, target, function.DeclaringType, function);
            translator.push(luaState, new SharpLua.Lua.lua_CFunction(wrapper.call));

            this[path] = translator.getObject(luaState, -1);
            LuaFunction f = GetFunction(path);

            LuaDLL.lua_settop(luaState, oldTop);

            return f;
        }

        /*
         * Compares the two values referenced by ref1 and ref2 for equality
         */
        internal bool compareRef(int ref1, int ref2)
        {
            int top = LuaDLL.lua_gettop(luaState);
            LuaDLL.lua_getref(luaState, ref1);
            LuaDLL.lua_getref(luaState, ref2);
            int equal = LuaDLL.lua_equal(luaState, -1, -2);
            LuaDLL.lua_settop(luaState, top);
            return (equal != 0);
        }

        internal void pushCSFunction(SharpLua.Lua.lua_CFunction function)
        {
            translator.pushFunction(luaState, function);
        }

        /// <summary>
        /// If the class has the ModuleAttribute, it registers all the methods with
        /// the LuaFunctionAttribute into this instance of SharpLua
        /// </summary>
        /// <param name="t"></param>
        /// <returns>The modules name, or null if there wasn't one</returns>
        /*public string RegisterModule(Type t)
        {
            string ret = null;
            if (t.IsClass == false)
                throw new ArgumentException("Not a class", "t");

            LuaModuleAttribute[] attribs = (LuaModuleAttribute[])t.GetCustomAttributes(typeof(LuaModuleAttribute), false);
            if (attribs.Length > 0)
            {
                if (attribs[0].ModuleName == "")
                    attribs[0].ModuleName = t.Name; // Default to class name if not specified
                // its a module
                string module = attribs[0].ModuleName;
                ret = module;
                this.NewTable(module);
                foreach (MethodInfo mi in t.GetMethods())
                {
                    LuaFunctionAttribute[] a2 = (LuaFunctionAttribute[])mi.GetCustomAttributes(typeof(LuaFunctionAttribute), false);
                    if (a2.Length > 0 && mi.IsStatic)
                    {
                        if (a2[0].FunctionName == "")
                            a2[0].FunctionName = mi.Name;
                        this.RegisterFunction(module + "." + a2[0].FunctionName, t, mi);
                    }
                }
            }
            return ret;
        }*/

        /// <summary>
        /// If the class has the LuaModuleAttribute, it registers all the methods with
        /// the LuaFunctionAttribute into this instance of SharpLua
        /// </summary>
        /// <param name="t"></param>
        /// <returns>The module, or null if there wasn't one</returns>
        public LuaTable RegisterModule(Type t)
        {
            LuaTable ret = null;
            if (t.IsClass == false)
                throw new ArgumentException("Not a class", "t");

            LuaModuleAttribute[] attribs = (LuaModuleAttribute[])t.GetCustomAttributes(typeof(LuaModuleAttribute), false);
            if (attribs.Length > 0)
            {
                if (attribs[0].ModuleName == "")
                    attribs[0].ModuleName = t.Name; // Default to class name if not specified
                // its a module
                string module = attribs[0].ModuleName;
                ret = this.NewTable(module);
                foreach (MethodInfo mi in t.GetMethods())
                {
                    LuaFunctionAttribute[] a2 = (LuaFunctionAttribute[])mi.GetCustomAttributes(typeof(LuaFunctionAttribute), false);
                    if (a2.Length > 0 && mi.IsStatic)
                    {
                        if (a2[0].FunctionName == "")
                            a2[0].FunctionName = mi.Name;
                        this.RegisterFunction(module + "." + a2[0].FunctionName, t, mi);
                    }
                }
            }
            return ret;
        }

        /// <summary>
        /// If the class of the object has the LuaModuleAttribute, it registers all
        /// the methods with the LuaFunctionAttribute into this instance of SharpLua
        /// </summary>
        /// <param name="t"></param>
        /// <returns>The module, or null if there wasn't one</returns>
        public LuaTable RegisterModule(object o)
        {
            LuaTable ret = null;
            var t = o.GetType();
  
            //Shouldn't ever happen, or can it?
            //if (t.IsClass == false)
            //    throw new ArgumentException("Not a class", "t");

            LuaModuleAttribute[] attribs = (LuaModuleAttribute[])t.GetCustomAttributes(typeof(LuaModuleAttribute), false);
            if (attribs.Length > 0)
            {
                if (attribs[0].ModuleName == "")
                    attribs[0].ModuleName = t.Name; // Default to class name if not specified
                // its a module
                string module = attribs[0].ModuleName;
                ret = this.NewTable(module);
                foreach (MethodInfo mi in t.GetMethods())
                {
                    LuaFunctionAttribute[] a2 = (LuaFunctionAttribute[])mi.GetCustomAttributes(typeof(LuaFunctionAttribute), false);
                    if (a2.Length > 0 && !mi.IsStatic)
                    {
                        if (a2[0].FunctionName == "")
                            a2[0].FunctionName = mi.Name;
                        this.RegisterFunction(module + "." + a2[0].FunctionName, o, mi);
                    }
                }
            }
            return ret;
        }

        public virtual void Dispose()
        {
            if (translator != null)
            {
                translator.pendingEvents.Dispose();
                translator = null;
            }

            this.Close();
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
        }

        #region IReturnListHandler Members

        int IReturnListHandler.NumResults
        {
            get { return -1; }
        }

        object[] IReturnListHandler.PopValues(Lua.LuaState L, int stktop)
        {
            return translator.popValues(L, stktop - 1);
        }

        #endregion
    }
}
