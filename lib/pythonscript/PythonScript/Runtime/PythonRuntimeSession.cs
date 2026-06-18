using System;
using Python.Runtime;
using System.Threading;

namespace PythonScript.Runtime
{
    /// <summary>
    /// 默认运行时会话实现，后续将替换现有的 <see cref="PythonHost"/> 静态访问模式。
    /// </summary>
    public sealed class PythonRuntimeSession : IPythonRuntimeSession
    {
        private readonly ReaderWriterLockSlim scopeLock = new(LockRecursionPolicy.SupportsRecursion);
        private readonly int creatingThreadId;
        private bool disposed;

        public PythonRuntimeSession()
        {
            creatingThreadId = Environment.CurrentManagedThreadId;
            EnsurePythonInitialized();
            Scope = CreateScopeInternal();
        }

        /// <inheritdoc />
        public PyModule Scope { get; private set; }

        /// <inheritdoc />
        public bool IsInitialized { get; private set; }

        /// <inheritdoc />
        public void WithGil(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            EnsureAccessOnCreatingThread();

            if (Volatile.Read(ref disposed))
            {
                throw new ObjectDisposedException(nameof(PythonRuntimeSession));
            }

            scopeLock.EnterReadLock();
            try
            {
                EnsureNotDisposed();
                PythonHost.WithGil(action);
            }
            finally
            {
                scopeLock.ExitReadLock();
            }
        }

        /// <inheritdoc />
        public T WithGil<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            EnsureAccessOnCreatingThread();

            if (Volatile.Read(ref disposed))
            {
                throw new ObjectDisposedException(nameof(PythonRuntimeSession));
            }

            scopeLock.EnterReadLock();
            try
            {
                EnsureNotDisposed();
                return PythonHost.WithGil(action);
            }
            finally
            {
                scopeLock.ExitReadLock();
            }
        }

        /// <inheritdoc />
        public PyModule CreateChildScope()
        {
            EnsureNotDisposed();
            EnsureAccessOnCreatingThread();
            return PythonHost.WithGil(CreateScopeCore);
        }

        /// <inheritdoc />
        public void ResetScope()
        {
            EnsureNotDisposed();
            EnsureAccessOnCreatingThread();

            scopeLock.EnterWriteLock();
            try
            {
                PythonHost.WithGil(() =>
                {
                    Scope.Dispose();
                });
                Scope = CreateScopeInternal();
            }
            finally
            {
                scopeLock.ExitWriteLock();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            EnsureAccessOnCreatingThread();

            scopeLock.EnterWriteLock();
            try
            {
                Volatile.Write(ref disposed, true);
                if (Scope != null)
                {
                    PythonHost.WithGil(() =>
                    {
                        Scope.Dispose();
                    });
                    Scope = null!;
                }
            }
            finally
            {
                scopeLock.ExitWriteLock();
                scopeLock.Dispose();
            }
        }

        private void EnsurePythonInitialized()
        {
            if (IsInitialized)
            {
                return;
            }

            if (!PythonHost.TryEnsureInitialized(out var error))
            {
                throw new InvalidOperationException($"Python 运行时初始化失败: {error}");
            }

            IsInitialized = true;
        }

        private PyModule CreateScopeInternal()
        {
            EnsureNotDisposed();
            EnsureAccessOnCreatingThread();
            return PythonHost.WithGil(CreateScopeCore);
        }

        private static PyModule CreateScopeCore()
        {
            var scope = Py.CreateScope();
            scope.Exec("import sys\nimport clr");
            return scope;
        }

        private void EnsureNotDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(PythonRuntimeSession));
            }
        }

        private void EnsureAccessOnCreatingThread()
        {
            if (Environment.CurrentManagedThreadId != creatingThreadId)
            {
                throw new InvalidOperationException("PythonRuntimeSession 只能在创建它的线程中访问，以避免 pythonnet GIL 死锁。");
            }
        }
    }
}
